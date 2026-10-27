// INSIDE native frame readback (Unity 5.0 legacy native plugin: UnitySetGraphicsDevice + UnityRenderEvent).
// Lives in INSIDE_Data\Plugins\InsideReadback.dll; Unity loads it on the first [DllImport("InsideReadback")] call.
// Events are RB_EVENT | op << 12 | session << 8 | seq & 0xFF and run on Unity's render thread in command order. INSIDE's
// engine build hands the device to the plugin but never calls UnityRenderEvent for GL.IssuePluginEvent (measured
// 2026-10-25), so the managed side queues the event with rb_push and clears a 1x1 marker render texture (rb_set_marker);
// a hook on the immediate context's ClearRenderTargetView runs the queued events when that clear reaches the GPU
// command stream, and a hook on OMSetRenderTargets finds the swap chain when Unity binds the back buffer. Events of
// another session (an aborted pass) are ignored:
//   grab  - copies the swap chain back buffer (GPU -> GPU) into a ring of staging textures and maps the copy made
//           `lag` grabs earlier, which the GPU has finished by then, into a ring of CPU frames (tight rows, top row
//           first). Neither the game thread nor the render thread waits for the GPU in steady state.
//   flush - maps every pending copy (end of a pass).
//   probe - finds the swap chain and records the back buffer size and format.
// The managed writer thread takes CPU frames in grab order (rb_take), streams them to ffmpeg and frees them
// (rb_release, any order). The render thread waits only for a free CPU frame (bounded, counted).
#define WIN32_LEAN_AND_MEAN
#define COBJMACROS
#define INITGUID
#include <windows.h>
#include <d3d11.h>
#include <dxgi.h>
#include <stdint.h>
#include <string.h>
#include <stdlib.h>
#include <stdio.h>
#include <stdarg.h>

#define RB_VERSION 1
#define RB_EVENT 0x52420000
#define RB_OP_GRAB 1
#define RB_OP_FLUSH 2
#define RB_OP_PROBE 3
#define RB_MAX_STAGE 8
#define RB_MAX_CPU 32
#define RB_SLOT_WAIT_MS 4000
#define RB_DROP_RING 64
#define RB_QUEUE 256

typedef void (STDMETHODCALLTYPE *om_set_fn)(ID3D11DeviceContext *, UINT, ID3D11RenderTargetView *const *, ID3D11DepthStencilView *);

#define RB_MAP_WAIT_MS 2000
#define RB_LOG_LINES 4000

enum { ERR_NONE, ERR_NO_DEVICE, ERR_NO_TARGET, ERR_NOT_SWAPCHAIN, ERR_FORMAT, ERR_STAGING, ERR_MAP, ERR_SLOT_TIMEOUT, ERR_ALLOC, ERR_CLOSED, ERR_MAP_TIMEOUT };

typedef struct {
    int version, device_type, has_device, render_thread, source, width, height, dxgi_format, samples;
    int stage_count, lag, cpu_count, pending, last_error, session, ignored;
    int64_t grabs, flushes, probes, frames, errors, dropped, produced, taken, released;
    double map_wait_ms, map_wait_max_ms, slot_wait_ms, slot_wait_max_ms, copy_ms, event_ms, event_max_ms;
    int64_t calls, device_calls, marker_hits, queued, hook_calls;
    int last_event, last_device_event, hooked, queue_depth;
} rb_stat;

typedef struct {
    int64_t seq, bytes;
    void *data;
    int slot, tag, width, height, pitch, dxgi_format, error, reserved;
} rb_frame;

typedef struct { ID3D11Texture2D *tex; int busy, tag; int64_t seq; } stage_slot;
typedef struct { uint8_t *buf; size_t cap; int busy, ready, tag, w, h, fmt, error; int64_t seq; } cpu_slot;

static struct {
    CRITICAL_SECTION cs;
    CONDITION_VARIABLE cv;
    int init;
    ID3D11Device *dev;
    ID3D11DeviceContext *ctx;
    IDXGISwapChain *swap;
    ID3D11Texture2D *resolve;
    stage_slot stage[RB_MAX_STAGE];
    int stage_w, stage_h, stage_fmt;
    int64_t stage_head, stage_tail;
    cpu_slot cpu[RB_MAX_CPU];
    int64_t dropped_seq[RB_DROP_RING];
    int open, reset_req;
    void *marker, *marker2;
    int queue[RB_QUEUE];
    int q_head, q_tail;
    void **vt_om;
    void *vt_base;
    volatile LONG n_om;
    ID3D11RenderTargetView *last_rtv;
    om_set_fn o_om;
    LARGE_INTEGER qpf;
    rb_stat st;
} g;

static void lock_init(void)
{
    if (g.init) return;
    InitializeCriticalSection(&g.cs);
    InitializeConditionVariable(&g.cv);
    QueryPerformanceFrequency(&g.qpf);
    g.st.version = RB_VERSION;
    g.st.stage_count = 4;
    g.st.lag = 2;
    g.st.cpu_count = 6;
    for (int i = 0; i < RB_DROP_RING; i++) g.dropped_seq[i] = -1;
    g.init = 1;
}

static void remove_hooks(void);
static HMODULE g_module;
static FILE *g_log;
static int g_log_lines, g_log_failed;
static CRITICAL_SECTION g_log_cs;

BOOL WINAPI DllMain(HINSTANCE h, DWORD reason, LPVOID r)
{
    if (reason == DLL_PROCESS_ATTACH) { g_module = h; InitializeCriticalSection(&g_log_cs); lock_init(); }
    if (reason == DLL_PROCESS_DETACH && r == NULL) remove_hooks();
    return TRUE;
}

static double now_ms(void)
{
    LARGE_INTEGER t;
    QueryPerformanceCounter(&t);
    return (double)t.QuadPart * 1000.0 / (double)g.qpf.QuadPart;
}

static void rlog(const char *fmt, ...)
{
    EnterCriticalSection(&g_log_cs);
    if (!g_log && !g_log_failed)
    {
        char path[MAX_PATH];
        DWORD n = GetModuleFileNameA(g_module, path, MAX_PATH);
        char *dot = n > 0 ? strrchr(path, '.') : NULL;
        if (dot && (size_t)(dot - path) + 5 < sizeof(path)) { strcpy(dot, ".log"); g_log = fopen(path, "w"); }
        if (!g_log) g_log_failed = 1;
    }
    if (g_log && g_log_lines < RB_LOG_LINES)
    {
        va_list a;
        va_start(a, fmt);
        fprintf(g_log, "%11.3f t%-6lu ", now_ms() / 1000.0, (unsigned long)GetCurrentThreadId());
        vfprintf(g_log, fmt, a);
        va_end(a);
        fputc('\n', g_log);
        if (++g_log_lines == RB_LOG_LINES) fputs("log line limit reached\n", g_log);
        fflush(g_log);
    }
    LeaveCriticalSection(&g_log_cs);
}

static void fail(int code)
{
    EnterCriticalSection(&g.cs);
    g.st.errors++;
    g.st.last_error = code;
    LeaveCriticalSection(&g.cs);
}

static void release_staging(void)
{
    for (int i = 0; i < RB_MAX_STAGE; i++)
    {
        if (g.stage[i].tex) ID3D11Texture2D_Release(g.stage[i].tex);
        memset(&g.stage[i], 0, sizeof(g.stage[i]));
    }
    if (g.resolve) { ID3D11Texture2D_Release(g.resolve); g.resolve = NULL; }
    g.stage_w = g.stage_h = g.stage_fmt = 0;
    g.stage_head = g.stage_tail = 0;
}

static void release_device(void)
{
    remove_hooks();
    release_staging();
    if (g.swap) { IDXGISwapChain_Release(g.swap); g.swap = NULL; }
    if (g.ctx) { ID3D11DeviceContext_Release(g.ctx); g.ctx = NULL; }
    g.dev = NULL;
    g.st.has_device = 0;
    g.st.source = 0;
}

__declspec(dllexport) void UnitySetGraphicsDevice(void *device, int deviceType, int eventType)
{
    lock_init();
    g.st.device_calls++;
    g.st.last_device_event = eventType;
    g.st.device_type = deviceType;
    rlog("UnitySetGraphicsDevice device %p type %d event %d", device, deviceType, eventType);
    if (eventType == 1 || eventType == 2) { release_device(); return; }
    if (eventType != 0 && eventType != 3) return;
    if (deviceType != 2 || !device) return;
    release_device();
    g.dev = (ID3D11Device *)device;
    ID3D11Device_GetImmediateContext(g.dev, &g.ctx);
    g.st.has_device = g.ctx != NULL;
}

static ID3D11Texture2D *find_swapchain_buffer(void)
{
    ID3D11Texture2D *t = NULL;
    if (g.swap)
    {
        if (SUCCEEDED(IDXGISwapChain_GetBuffer(g.swap, 0, &IID_ID3D11Texture2D, (void **)&t))) return t;
        IDXGISwapChain_Release(g.swap);
        g.swap = NULL;
        g.st.source = 0;
    }
    ID3D11RenderTargetView *rtv = NULL;
    ID3D11DeviceContext_OMGetRenderTargets(g.ctx, 1, &rtv, NULL);
    if (!rtv) { fail(ERR_NO_TARGET); return NULL; }
    ID3D11Resource *res = NULL;
    ID3D11RenderTargetView_GetResource(rtv, &res);
    ID3D11RenderTargetView_Release(rtv);
    if (!res) { fail(ERR_NO_TARGET); return NULL; }
    IDXGISurface *surf = NULL;
    IDXGISwapChain *sc = NULL;
    if (SUCCEEDED(ID3D11Resource_QueryInterface(res, &IID_IDXGISurface, (void **)&surf)))
    {
        if (FAILED(IDXGISurface_GetParent(surf, &IID_IDXGISwapChain, (void **)&sc))) sc = NULL;
        IDXGISurface_Release(surf);
    }
    ID3D11Resource_Release(res);
    if (!sc) { fail(ERR_NOT_SWAPCHAIN); return NULL; }
    g.swap = sc;
    g.st.source = 1;
    if (SUCCEEDED(IDXGISwapChain_GetBuffer(g.swap, 0, &IID_ID3D11Texture2D, (void **)&t))) return t;
    fail(ERR_NOT_SWAPCHAIN);
    return NULL;
}

static int supported_format(int f)
{
    return f == DXGI_FORMAT_R8G8B8A8_UNORM || f == DXGI_FORMAT_R8G8B8A8_UNORM_SRGB || f == DXGI_FORMAT_B8G8R8A8_UNORM ||
           f == DXGI_FORMAT_B8G8R8A8_UNORM_SRGB || f == DXGI_FORMAT_B8G8R8X8_UNORM || f == DXGI_FORMAT_B8G8R8X8_UNORM_SRGB;
}

static int ensure_staging(const D3D11_TEXTURE2D_DESC *src)
{
    if (g.stage_w == (int)src->Width && g.stage_h == (int)src->Height && g.stage_fmt == (int)src->Format && g.stage[0].tex) return 1;
    release_staging();
    D3D11_TEXTURE2D_DESC d;
    memset(&d, 0, sizeof(d));
    d.Width = src->Width; d.Height = src->Height; d.MipLevels = 1; d.ArraySize = 1; d.Format = src->Format;
    d.SampleDesc.Count = 1; d.Usage = D3D11_USAGE_STAGING; d.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    for (int i = 0; i < g.st.stage_count; i++)
        if (FAILED(ID3D11Device_CreateTexture2D(g.dev, &d, NULL, &g.stage[i].tex))) { release_staging(); fail(ERR_STAGING); return 0; }
    if (src->SampleDesc.Count > 1)
    {
        d.Usage = D3D11_USAGE_DEFAULT; d.CPUAccessFlags = 0;
        if (FAILED(ID3D11Device_CreateTexture2D(g.dev, &d, NULL, &g.resolve))) { release_staging(); fail(ERR_STAGING); return 0; }
    }
    g.stage_w = (int)src->Width; g.stage_h = (int)src->Height; g.stage_fmt = (int)src->Format;
    return 1;
}

static cpu_slot *acquire_cpu(int64_t seq)
{
    int n = g.st.cpu_count;
    cpu_slot *c = &g.cpu[seq % n];
    double t0 = now_ms();
    EnterCriticalSection(&g.cs);
    while (c->busy && g.open)
        if (!SleepConditionVariableCS(&g.cv, &g.cs, 50) && now_ms() - t0 > RB_SLOT_WAIT_MS) break;
    double w = now_ms() - t0;
    g.st.slot_wait_ms += w;
    if (w > g.st.slot_wait_max_ms) g.st.slot_wait_max_ms = w;
    if (c->busy || !g.open)
    {
        g.st.dropped++;
        g.st.errors++;
        g.dropped_seq[seq % RB_DROP_RING] = seq;
        WakeAllConditionVariable(&g.cv);
        g.st.last_error = g.open ? ERR_SLOT_TIMEOUT : ERR_CLOSED;
        LeaveCriticalSection(&g.cs);
        return NULL;
    }
    c->busy = 1;
    c->ready = 0;
    LeaveCriticalSection(&g.cs);
    return c;
}

static void publish(cpu_slot *c, int64_t seq, int tag, int w, int h, int fmt, int error)
{
    EnterCriticalSection(&g.cs);
    c->seq = seq; c->tag = tag; c->w = w; c->h = h; c->fmt = fmt; c->error = error;
    c->ready = 1;
    g.st.produced++;
    if (!error) g.st.frames++;
    WakeAllConditionVariable(&g.cv);
    LeaveCriticalSection(&g.cs);
    if (seq < 8 || error) rlog("publish seq %lld error %d", (long long)seq, error);
}

static void emit_error(int64_t seq, int tag, int code)
{
    cpu_slot *c = acquire_cpu(seq);
    if (c) publish(c, seq, tag, 0, 0, 0, code);
    rlog("error frame seq %lld code %d", (long long)seq, code);
}

static int map_oldest(int wait_ms)
{
    stage_slot *s = &g.stage[g.stage_tail % g.st.stage_count];
    D3D11_MAPPED_SUBRESOURCE m;
    double t0 = now_ms();
    HRESULT hr;
    for (;;)
    {
        hr = ID3D11DeviceContext_Map(g.ctx, (ID3D11Resource *)s->tex, 0, D3D11_MAP_READ, D3D11_MAP_FLAG_DO_NOT_WAIT, &m);
        if (hr != DXGI_ERROR_WAS_STILL_DRAWING || now_ms() - t0 >= wait_ms) break;
        Sleep(0);
    }
    if (hr == DXGI_ERROR_WAS_STILL_DRAWING && wait_ms == 0) return 0;
    double t1 = now_ms();
    int64_t seq = s->seq;
    int tag = s->tag;
    g.stage_tail++;
    s->busy = 0;
    EnterCriticalSection(&g.cs);
    g.st.map_wait_ms += t1 - t0;
    if (t1 - t0 > g.st.map_wait_max_ms) g.st.map_wait_max_ms = t1 - t0;
    LeaveCriticalSection(&g.cs);
    rlog("map seq %lld hr 0x%08lx after %.2f ms (wait %d)", (long long)seq, (unsigned long)hr, t1 - t0, wait_ms);
    if (FAILED(hr))
    {
        int code = hr == DXGI_ERROR_WAS_STILL_DRAWING ? ERR_MAP_TIMEOUT : ERR_MAP;
        fail(code);
        emit_error(seq, tag, code);
        return 1;
    }
    cpu_slot *c = acquire_cpu(seq);
    if (!c) { ID3D11DeviceContext_Unmap(g.ctx, (ID3D11Resource *)s->tex, 0); return 1; }
    size_t row = (size_t)g.stage_w * 4, need = row * (size_t)g.stage_h;
    if (c->cap < need)
    {
        free(c->buf);
        c->buf = (uint8_t *)malloc(need);
        c->cap = c->buf ? need : 0;
    }
    if (!c->buf)
    {
        ID3D11DeviceContext_Unmap(g.ctx, (ID3D11Resource *)s->tex, 0);
        fail(ERR_ALLOC);
        publish(c, seq, tag, g.stage_w, g.stage_h, g.stage_fmt, ERR_ALLOC);
        return 1;
    }
    const uint8_t *src = (const uint8_t *)m.pData;
    if (m.RowPitch == row) memcpy(c->buf, src, need);
    else for (int y = 0; y < g.stage_h; y++) memcpy(c->buf + row * y, src + (size_t)m.RowPitch * y, row);
    ID3D11DeviceContext_Unmap(g.ctx, (ID3D11Resource *)s->tex, 0);
    double t2 = now_ms();
    EnterCriticalSection(&g.cs);
    g.st.copy_ms += t2 - t1;
    LeaveCriticalSection(&g.cs);
    publish(c, seq, tag, g.stage_w, g.stage_h, g.stage_fmt, 0);
    return 1;
}

static void flush_all(int wait_ms)
{
    while (g.stage_tail < g.stage_head) if (!map_oldest(wait_ms)) break;
}

static void discard_stale(void)
{
    if (!g.reset_req) return;
    for (int i = 0; i < RB_MAX_STAGE; i++) g.stage[i].busy = 0;
    g.stage_tail = g.stage_head;
    g.reset_req = 0;
}

static void do_grab(int tag)
{
    int64_t seq;
    EnterCriticalSection(&g.cs);
    seq = g.st.grabs++;
    LeaveCriticalSection(&g.cs);
    if (!g.ctx) { fail(ERR_NO_DEVICE); emit_error(seq, tag, ERR_NO_DEVICE); return; }
    ID3D11Texture2D *bb = find_swapchain_buffer();
    if (!bb) { emit_error(seq, tag, g.st.last_error); return; }
    D3D11_TEXTURE2D_DESC d;
    ID3D11Texture2D_GetDesc(bb, &d);
    g.st.width = (int)d.Width; g.st.height = (int)d.Height; g.st.dxgi_format = (int)d.Format; g.st.samples = (int)d.SampleDesc.Count;
    if (!supported_format((int)d.Format)) { ID3D11Texture2D_Release(bb); fail(ERR_FORMAT); emit_error(seq, tag, ERR_FORMAT); return; }
    if (g.stage[0].tex && (g.stage_w != (int)d.Width || g.stage_h != (int)d.Height || g.stage_fmt != (int)d.Format)) flush_all(RB_MAP_WAIT_MS);
    if (!ensure_staging(&d)) { ID3D11Texture2D_Release(bb); emit_error(seq, tag, ERR_STAGING); return; }
    stage_slot *s = &g.stage[g.stage_head % g.st.stage_count];
    if (s->busy) map_oldest(RB_MAP_WAIT_MS);
    if (g.resolve)
    {
        ID3D11DeviceContext_ResolveSubresource(g.ctx, (ID3D11Resource *)g.resolve, 0, (ID3D11Resource *)bb, 0, d.Format);
        ID3D11DeviceContext_CopyResource(g.ctx, (ID3D11Resource *)s->tex, (ID3D11Resource *)g.resolve);
    }
    else ID3D11DeviceContext_CopyResource(g.ctx, (ID3D11Resource *)s->tex, (ID3D11Resource *)bb);
    ID3D11Texture2D_Release(bb);
    ID3D11DeviceContext_Flush(g.ctx);
    s->busy = 1; s->seq = seq; s->tag = tag;
    g.stage_head++;
    rlog("grab seq %lld tag 0x%03x copied, head %lld tail %lld", (long long)seq, tag, (long long)g.stage_head, (long long)g.stage_tail);
    while (g.stage_head - g.stage_tail > g.st.lag) if (!map_oldest(0)) break;
}

static void do_probe(void)
{
    g.st.probes++;
    if (!g.ctx) { fail(ERR_NO_DEVICE); return; }
    ID3D11Texture2D *bb = find_swapchain_buffer();
    if (!bb) return;
    D3D11_TEXTURE2D_DESC d;
    ID3D11Texture2D_GetDesc(bb, &d);
    g.st.width = (int)d.Width; g.st.height = (int)d.Height; g.st.dxgi_format = (int)d.Format; g.st.samples = (int)d.SampleDesc.Count;
    ID3D11Texture2D_Release(bb);
}

static void handle_event(int eventID)
{
    g.st.last_event = eventID;
    if ((eventID & 0xFFFF0000) != RB_EVENT) return;
    int op = (eventID >> 12) & 0xF, tag = eventID & 0xFFF;
    if (op != RB_OP_PROBE && ((tag >> 8) & 0xF) != g.st.session) { g.st.ignored++; rlog("ignored event 0x%08x (session %d)", eventID, g.st.session); return; }
    if (op != RB_OP_GRAB || g.st.grabs < 8) rlog("event op %d tag 0x%03x, pending %lld", op, tag, (long long)(g.stage_head - g.stage_tail));
    double t0 = now_ms();
    g.st.render_thread = (int)GetCurrentThreadId();
    discard_stale();
    if (op == RB_OP_GRAB) do_grab(tag);
    else if (op == RB_OP_FLUSH) { g.st.flushes++; flush_all(0); }
    else if (op == RB_OP_PROBE) do_probe();
    double t = now_ms() - t0;
    EnterCriticalSection(&g.cs);
    g.st.event_ms += t;
    if (t > g.st.event_max_ms) g.st.event_max_ms = t;
    g.st.pending = (int)(g.stage_head - g.stage_tail);
    LeaveCriticalSection(&g.cs);
}

__declspec(dllexport) void UnityRenderEvent(int eventID)
{
    lock_init();
    g.st.calls++;
    handle_event(eventID);
}

static int pop_event(int *ev)
{
    int ok = 0;
    EnterCriticalSection(&g.cs);
    if (g.q_tail != g.q_head) { *ev = g.queue[g.q_tail % RB_QUEUE]; g.q_tail++; ok = 1; }
    g.st.queue_depth = g.q_head - g.q_tail;
    LeaveCriticalSection(&g.cs);
    return ok;
}

static void try_swapchain(ID3D11RenderTargetView *rtv)
{
    ID3D11Resource *res = NULL;
    ID3D11RenderTargetView_GetResource(rtv, &res);
    if (!res) return;
    IDXGISurface *surf = NULL;
    IDXGISwapChain *sc = NULL;
    if (SUCCEEDED(ID3D11Resource_QueryInterface(res, &IID_IDXGISurface, (void **)&surf)))
    {
        if (FAILED(IDXGISurface_GetParent(surf, &IID_IDXGISwapChain, (void **)&sc))) sc = NULL;
        IDXGISurface_Release(surf);
    }
    ID3D11Resource_Release(res);
    if (sc) { g.swap = sc; g.st.source = 1; rlog("swap chain found %p", (void *)sc); }
}

static int is_marker(ID3D11RenderTargetView *rtv)
{
    ID3D11Resource *res = NULL;
    ID3D11RenderTargetView_GetResource(rtv, &res);
    int hit = res && ((void *)res == g.marker || (void *)res == g.marker2);
    if (res) ID3D11Resource_Release(res);
    return hit;
}

static void describe_rtv(const char *what, ID3D11RenderTargetView *rtv)
{
    ID3D11Resource *res = NULL;
    ID3D11RenderTargetView_GetResource(rtv, &res);
    if (!res) { rlog("%s: rtv %p without resource", what, (void *)rtv); return; }
    ID3D11Texture2D *t = NULL;
    D3D11_TEXTURE2D_DESC d;
    memset(&d, 0, sizeof(d));
    if (SUCCEEDED(ID3D11Resource_QueryInterface(res, &IID_ID3D11Texture2D, (void **)&t))) { ID3D11Texture2D_GetDesc(t, &d); ID3D11Texture2D_Release(t); }
    ID3D11Texture2D *bb = NULL;
    int is_bb = 0;
    if (g.swap && SUCCEEDED(IDXGISwapChain_GetBuffer(g.swap, 0, &IID_ID3D11Texture2D, (void **)&bb))) { is_bb = (void *)bb == (void *)res; ID3D11Texture2D_Release(bb); }
    rlog("%s: res %p %ux%u format %d samples %u bind 0x%x, swap chain buffer %s", what, (void *)res, d.Width, d.Height, (int)d.Format, d.SampleDesc.Count, d.BindFlags, is_bb ? "YES" : "no");
    ID3D11Resource_Release(res);
}

static void STDMETHODCALLTYPE hk_om(ID3D11DeviceContext *c, UINT n, ID3D11RenderTargetView *const *rtvs, ID3D11DepthStencilView *dsv)
{
    InterlockedIncrement(&g.n_om);
    if (c == g.ctx && n > 0 && rtvs && rtvs[0])
    {
        if (!g.swap) try_swapchain(rtvs[0]);
        int marker_hit = g.marker && is_marker(rtvs[0]);
        if (!marker_hit) g.last_rtv = rtvs[0];
        if (marker_hit)
        {
            if (g.st.marker_hits < 6 && g.last_rtv) describe_rtv("last target before marker", g.last_rtv);
            g.st.marker_hits++;
            if (g.st.marker_hits <= 24) rlog("marker bound (hit %lld), queue %d", (long long)g.st.marker_hits, g.q_head - g.q_tail);
            int ev;
            while (pop_event(&ev)) handle_event(ev);
        }
    }
    g.o_om(c, n, rtvs, dsv);
}

static void patch_slot(void **slot, void *fn)
{
    DWORD old;
    if (!VirtualProtect(slot, sizeof(void *), PAGE_READWRITE, &old)) return;
    InterlockedExchangePointer(slot, fn);
    VirtualProtect(slot, sizeof(void *), old, &old);
}

static void install_hooks(void)
{
    if (!g.ctx) return;
    ID3D11DeviceContextVtbl *vt = (ID3D11DeviceContextVtbl *)g.ctx->lpVtbl;
    void **slot = (void **)&vt->OMSetRenderTargets;
    if (g.st.hooked && g.vt_om == slot && *slot == (void *)hk_om) return;
    if (*slot != (void *)hk_om)
    {
        if (g.st.hooked) g.st.ignored++;
        g.o_om = (om_set_fn)*slot;
        g.vt_om = slot;
        g.vt_base = (void *)vt;
        patch_slot(slot, (void *)hk_om);
    }
    g.st.hooked = *slot == (void *)hk_om;
    rlog("OMSetRenderTargets hook %s on context %p (table %p, original %p)", g.st.hooked ? "installed" : "FAILED", (void *)g.ctx, (void *)vt, (void *)g.o_om);
}

static void remove_hooks(void)
{
    if (!g.st.hooked) return;
    if (*g.vt_om == (void *)hk_om) patch_slot(g.vt_om, (void *)g.o_om);
    g.st.hooked = 0;
}

__declspec(dllexport) void rb_set_marker(void *tex, void *tex2) { g.marker = tex; g.marker2 = tex2; rlog("markers set %p %p", tex, tex2); }

__declspec(dllexport) int rb_push(int eventID)
{
    lock_init();
    EnterCriticalSection(&g.cs);
    int ok = g.q_head - g.q_tail < RB_QUEUE;
    if (ok) { g.queue[g.q_head % RB_QUEUE] = eventID; g.q_head++; g.st.queued++; }
    g.st.queue_depth = g.q_head - g.q_tail;
    LeaveCriticalSection(&g.cs);
    return ok;
}

__declspec(dllexport) int rb_version(void) { return RB_VERSION; }

__declspec(dllexport) int rb_open(int stages, int lag, int cpu)
{
    lock_init();
    EnterCriticalSection(&g.cs);
    for (int i = 0; i < RB_MAX_CPU; i++) if (g.cpu[i].busy) { LeaveCriticalSection(&g.cs); return 0; }
    for (int i = 0; i < RB_DROP_RING; i++) g.dropped_seq[i] = -1;
    g.reset_req = 1;
    g.st.session = (g.st.session + 1) & 0xF;
    if (g.st.session == 0) g.st.session = 1;
    g.st.stage_count = stages < 2 ? 2 : stages > RB_MAX_STAGE ? RB_MAX_STAGE : stages;
    g.st.lag = lag < 0 ? 0 : lag >= g.st.stage_count ? g.st.stage_count - 1 : lag;
    g.st.cpu_count = cpu < 2 ? 2 : cpu > RB_MAX_CPU ? RB_MAX_CPU : cpu;
    g.q_tail = g.q_head;
    install_hooks();
    g.st.grabs = g.st.flushes = g.st.frames = g.st.errors = g.st.dropped = g.st.produced = g.st.taken = g.st.released = 0;
    g.st.map_wait_ms = g.st.map_wait_max_ms = g.st.slot_wait_ms = g.st.slot_wait_max_ms = g.st.copy_ms = g.st.event_ms = g.st.event_max_ms = 0;
    g.st.last_error = 0;
    g.open = 1;
    int session = g.st.session;
    LeaveCriticalSection(&g.cs);
    rlog("rb_open session %d stages %d lag %d cpu %d", session, g.st.stage_count, g.st.lag, g.st.cpu_count);
    return session;
}

__declspec(dllexport) void rb_close(void)
{
    EnterCriticalSection(&g.cs);
    g.open = 0;
    WakeAllConditionVariable(&g.cv);
    LeaveCriticalSection(&g.cs);
    rlog("rb_close: grabs %lld produced %lld taken %lld released %lld", (long long)g.st.grabs, (long long)g.st.produced, (long long)g.st.taken, (long long)g.st.released);
}

__declspec(dllexport) void rb_free_buffers(void)
{
    EnterCriticalSection(&g.cs);
    for (int i = 0; i < RB_MAX_CPU; i++)
        if (!g.cpu[i].busy) { free(g.cpu[i].buf); g.cpu[i].buf = NULL; g.cpu[i].cap = 0; }
    LeaveCriticalSection(&g.cs);
}

__declspec(dllexport) int rb_take(int timeout_ms, rb_frame *out)
{
    double t0 = now_ms();
    EnterCriticalSection(&g.cs);
    for (;;)
    {
        cpu_slot *c = &g.cpu[g.st.taken % g.st.cpu_count];
        if (g.dropped_seq[g.st.taken % RB_DROP_RING] == g.st.taken)
        {
            g.dropped_seq[g.st.taken % RB_DROP_RING] = -1;
            memset(out, 0, sizeof(*out));
            out->slot = -1;
            out->seq = g.st.taken;
            out->error = ERR_SLOT_TIMEOUT;
            g.st.taken++;
            LeaveCriticalSection(&g.cs);
            return 1;
        }
        if (c->busy && c->ready && c->seq == g.st.taken)
        {
            c->ready = 0;
            out->slot = (int)(g.st.taken % g.st.cpu_count);
            out->seq = c->seq; out->tag = c->tag; out->width = c->w; out->height = c->h; out->pitch = c->w * 4;
            out->dxgi_format = c->fmt; out->error = c->error;
            out->data = c->error ? NULL : c->buf;
            out->bytes = c->error ? 0 : (int64_t)c->w * c->h * 4;
            g.st.taken++;
            LeaveCriticalSection(&g.cs);
            return 1;
        }
        double left = timeout_ms - (now_ms() - t0);
        if (left <= 0) { LeaveCriticalSection(&g.cs); return 0; }
        SleepConditionVariableCS(&g.cv, &g.cs, (DWORD)(left < 1 ? 1 : left));
    }
}

__declspec(dllexport) void rb_release(int slot)
{
    if (slot < 0 || slot >= RB_MAX_CPU) return;
    EnterCriticalSection(&g.cs);
    if (g.cpu[slot].busy) { g.cpu[slot].busy = 0; g.cpu[slot].ready = 0; g.st.released++; }
    WakeAllConditionVariable(&g.cv);
    LeaveCriticalSection(&g.cs);
}

__declspec(dllexport) int rb_stat_get(rb_stat *out)
{
    lock_init();
    g.st.hook_calls = g.n_om;
    if (g.st.hooked && g.vt_om && *g.vt_om != (void *)hk_om) { rlog("OMSetRenderTargets slot was rewritten (%p), re-hooking", *g.vt_om); install_hooks(); }
    EnterCriticalSection(&g.cs);
    *out = g.st;
    LeaveCriticalSection(&g.cs);
    return (int)sizeof(rb_stat);
}
