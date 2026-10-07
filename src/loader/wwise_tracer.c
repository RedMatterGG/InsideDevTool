// WwiseTracer — observation-only tracing of INSIDE's calls into AkSoundEngine.dll.
//
// How it hooks: the game's managed Wwise wrapper calls AkSoundEngine.dll through P/Invoke
// ([DllImport("AkSoundEngine")] CSharp_*). Mono resolves each entry point lazily with
// kernel32!GetProcAddress, imported by mono.dll. We patch mono.dll's GetProcAddress import;
// when Mono asks AkSoundEngine.dll for one of the entry points in wt_hooks.inc, it gets our
// wrapper instead. The wrapper calls the original with untouched arguments, returns its exact
// result, and pushes one fixed-size POD record into a bounded ring buffer. No string
// formatting, no allocation, no locks in the hot path. InsideDev drains the ring on the main
// thread (WT_Drain) and does all naming / database / UI work.
//
// By default nothing here changes IDs, suppresses calls or injects calls (observation only). The opt-in native
// audio rules (WT_Rule*, off until InsideDev enables them) are the single exception, see below.
// Only the managed -> native boundary is observed; Wwise-internal activity is visible only through its callbacks.
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdint.h>
#include <string.h>
#include <wchar.h>
#include <stdio.h>

void wt_log(const char *fmt, ...);   // version_proxy.c

#define WT_EXPORT __declspec(dllexport)
#define WT_VERSION 3

// ---------------------------------------------------------------- record + ring
#pragma pack(push, 8)
typedef struct WtRec {
    volatile uint32_t seq;   // 0: index+1 once the slot is complete
    uint16_t type;           // 4: WT_* record type
    uint16_t api;            // 6: hook index (which entry point)
    uint32_t thread;         // 8
    int32_t frame;           // 12: managed frame (WT_SetFrame)
    int64_t qpc;             // 16: QueryPerformanceCounter
    uint32_t gameObj;        // 24: Wwise game object id (= Unity GetInstanceID in INSIDE)
    uint32_t eventId;        // 28
    uint32_t playingId;      // 32
    uint32_t groupId;        // 36: switch/state group, rtpc, trigger, action type, prepare type
    uint32_t valueId;        // 40: switch/state value, requested playing id, first prepared id
    float fvalue;            // 44: rtpc value / seek position
    uint32_t bankId;         // 48
    uint32_t cbType;         // 52: PostEvent callback flags / callback type
    uint64_t cookie;         // 56
    int32_t ret;             // 64: return value (AKRESULT, playing id, id)
    int32_t i1;              // 68: ms / pool / mask / marker id / label hash
    int32_t i2;              // 72: curve / count
    uint32_t flags;          // 76
    float pos[3];            // 80
    uint32_t pad;            // 92 (internal)
    uint16_t name[32];       // 96: wide string argument (truncated)
} WtRec;                     // 160
#pragma pack(pop)

enum {
    WT_POST_EVENT = 1, WT_EXEC_ACTION = 3, WT_SEEK = 4, WT_STOP_ALL = 5, WT_STOP_PLAYING = 6,
    WT_SET_RTPC = 7, WT_SET_RTPC_PID = 8, WT_RESET_RTPC = 9, WT_SET_SWITCH = 10, WT_POST_TRIGGER = 12,
    WT_SET_STATE = 13, WT_REGISTER = 15, WT_UNREGISTER = 16, WT_SET_POS = 17, WT_LISTENER_POS = 18,
    WT_LOAD_BANK = 19, WT_UNLOAD_BANK = 20, WT_PREPARE_EVENT = 21, WT_CANCEL_CB = 22, WT_CALLBACK = 23,
    WT_CB_INIT = 24, WT_ID_FROM_STRING = 26, WT_PREPARE_SYNCS = 27
};

#define RING_BITS 14
#define RING_N (1u << RING_BITS)
static WtRec g_ring[RING_N];
static volatile LONG g_head;
static uint32_t g_tail;
static volatile LONG g_dropped, g_frame, g_total, g_render, g_cbItems, g_cbBad;
static volatile LONG g_level = 1;      // 0 counts only, 1 normal, 2 +positions, 3 raw (no dedupe)

static WtRec *wt_begin(uint16_t type, uint16_t api) {
    uint32_t idx = (uint32_t)InterlockedIncrement(&g_head) - 1;
    WtRec *r = &g_ring[idx & (RING_N - 1)];
    r->seq = 0;
    MemoryBarrier();
    memset((char *)r + 4, 0, sizeof(WtRec) - 4);
    r->type = type; r->api = api;
    r->thread = GetCurrentThreadId();
    r->frame = g_frame;
    LARGE_INTEGER q; QueryPerformanceCounter(&q); r->qpc = q.QuadPart;
    r->pad = idx;
    return r;
}
static void wt_end(WtRec *r) {
    uint32_t idx = r->pad;
    r->pad = 0;
    MemoryBarrier();
    r->seq = idx + 1;
    InterlockedIncrement(&g_total);
}
static void wt_name(WtRec *r, const wchar_t *a, const wchar_t *b) {
    int n = 0;
    if (a) for (; *a && n < 31; ++a) r->name[n++] = (uint16_t)*a;
    if (b && n < 30) { r->name[n++] = '/'; for (; *b && n < 31; ++b) r->name[n++] = (uint16_t)*b; }
    r->name[n] = 0;
}

typedef struct Hook { const char *name; void *hook; void **orig; volatile LONG calls; int resolved; } Hook;

// ---------------------------------------------------------------- opt-in native rules (OFF by default)
// InsideDev's audio rules (mute / nuke / replace / forced switch, state, RTPC) mirrored into fixed tables so they
// also apply to calls that reach AkSoundEngine without passing the managed wrapper. While g_enforce is 0 every
// wrapper passes its call through untouched (the observation-only behaviour). Keys are Wwise ids (FNV-1 of the
// lower-case name, hashed here for the string-argument entry points). No allocation, no strings, no locks.
#define WR_N 512
typedef struct WrSlot { volatile uint32_t key; volatile uint32_t val; } WrSlot;
static WrSlot g_rNuke[WR_N], g_rMute[WR_N], g_rRep[WR_N], g_rSw[WR_N], g_rSt[WR_N], g_rRtpc[WR_N];
static volatile LONG g_enforce, g_ruleCount, g_nNuked, g_nMuted, g_nReplaced, g_nForced;
static HMODULE g_ak;
static int wr_get(WrSlot *t, uint32_t k, uint32_t *v) {
    if (!k) return 0;
    uint32_t h = (k * 2654435761u) >> 23;
    for (uint32_t i = 0; i < WR_N; ++i, h = (h + 1) & (WR_N - 1)) {
        uint32_t kk = t[h & (WR_N - 1)].key;
        if (kk == k) { if (v) *v = t[h & (WR_N - 1)].val; return 1; }
        if (!kk) return 0;
    }
    return 0;
}
static int wr_put(WrSlot *t, uint32_t k, uint32_t v) {
    if (!k) return 0;
    uint32_t h = (k * 2654435761u) >> 23;
    for (uint32_t i = 0; i < WR_N; ++i, h = (h + 1) & (WR_N - 1)) {
        WrSlot *s = &t[h & (WR_N - 1)];
        if (s->key == k || !s->key) { s->val = v; MemoryBarrier(); s->key = k; return 1; }
    }
    return 0;
}
static uint32_t wr_hashw(const wchar_t *s) {
    uint32_t h = 2166136261u;
    if (!s) return 0;
    for (; *s; ++s) { wchar_t c = *s; if (c >= L'A' && c <= L'Z') c = (wchar_t)(c + 32); h *= 16777619u; h ^= (uint8_t)c; }
    return h;
}
// the real entry point for hook i (resolved from AkSoundEngine.dll if the game has not resolved it yet)
static void *wr_orig(int i);
static void wr_stop(uint32_t pid);
static void wr_note(uint16_t type, int api, uint32_t ev, uint32_t go);
#include "wt_hooks.inc"

static void *wr_orig(int i) {
    if (*g_hooks[i].orig) return *g_hooks[i].orig;
    if (!g_ak) return 0;
    void *p = (void *)GetProcAddress(g_ak, g_hooks[i].name);
    if (p) *g_hooks[i].orig = p;          // same address Mono would get: consistent with hk_monoGPA's check
    return p;
}
static void (*g_stopFn)(uint32_t);
static void wr_stop(uint32_t pid) {
    if (!g_stopFn && g_ak) g_stopFn = (void (*)(uint32_t))GetProcAddress(g_ak, "CSharp_StopPlayingID__SWIG_2");
    if (g_stopFn) g_stopFn(pid);
}
// a post blocked by a native rule still leaves a trace record (ret 0, flags bit 31 = blocked by rule)
static void wr_note(uint16_t type, int api, uint32_t ev, uint32_t go) {
    if (g_level <= 0) return;
    WtRec *q = wt_begin(type, (uint16_t)api); q->eventId = ev; q->gameObj = go; q->ret = 0; q->flags = 0x80000000u; wt_end(q);
}

// ---------------------------------------------------------------- hand-written hooks
// RenderAudio: once per game frame; counted only.
static int32_t (*o_render)(void);
static int32_t h_render(void) { InterlockedIncrement(&g_render); return o_render(); }

// GetIDFromString: the game's own name -> id hashing. Deduplicated (direct-mapped cache) at level < 3.
static uint32_t (*o_idstr)(const wchar_t *);
static uint32_t g_idSeen[4096];
static uint32_t h_idstr(const wchar_t *s) {
    uint32_t r = o_idstr(s);
    if (g_level > 0) {
        uint32_t slot = (r ^ (r >> 12)) & 4095;
        if (g_level >= 3 || g_idSeen[slot] != r) {
            g_idSeen[slot] = r;
            WtRec *q = wt_begin(WT_ID_FROM_STRING, 0xFFF0);
            q->ret = (int32_t)r; q->valueId = r; wt_name(q, s, 0);
            wt_end(q);
        }
    }
    return r;
}

// Callback serializer: Wwise writes callbacks into a buffer handed over at Init; the game's
// AkCallbackManager.PostCallbacks takes Lock(), walks a linked list, dispatches, Unlock().
// We walk the same list read-only right after Lock() returns (bounds-checked to the buffer).
static int32_t (*o_cbInit)(void *, uint32_t);
static void *(*o_cbLock)(void);
static uint8_t *g_cbBase; static uint32_t g_cbSize;
static int32_t h_cbInit(void *mem, uint32_t size) {
    int32_t r = o_cbInit(mem, size);
    g_cbBase = (uint8_t *)mem; g_cbSize = size;
    WtRec *q = wt_begin(WT_CB_INIT, 0xFFF1); q->cookie = (uint64_t)(uintptr_t)mem; q->i1 = (int32_t)size; q->ret = r; wt_end(q);
    return r;
}
static int inbuf(const uint8_t *p, uint32_t n) { return g_cbBase && p >= g_cbBase && p + n <= g_cbBase + g_cbSize; }
static void *h_cbLock(void) {
    void *r = o_cbLock();
    if (!r || g_level <= 0) return r;
    const uint8_t *it = (const uint8_t *)r;
    for (int guard = 0; it && guard < 512; ++guard) {
        if (!inbuf(it, 24 + 24)) { InterlockedIncrement(&g_cbBad); break; }
        uint64_t pkg = *(const uint64_t *)it;
        const uint8_t *next = *(const uint8_t *const *)(it + 8);
        uint32_t type = *(const uint32_t *)(it + 16);
        const uint8_t *p = it + 24;
        WtRec *q = wt_begin(WT_CALLBACK, 0xFFF2);
        q->cbType = type; q->cookie = pkg;
        if (type == 0x40000000u) {                // AK_Bank: bankID, inMemoryPtr, result, pool
            q->bankId = *(const uint32_t *)p; q->ret = *(const int32_t *)(p + 16); q->i1 = *(const int32_t *)(p + 20);
        } else if (type == 0x20000000u) {         // AK_Monitoring: errorCode, level, playingID, gameObj
            q->i1 = *(const int32_t *)p; q->i2 = *(const int32_t *)(p + 4); q->playingId = *(const uint32_t *)(p + 8);
            q->gameObj = (uint32_t)*(const uint64_t *)(p + 16);
        } else {
            // event-type payload as serialized by this Wwise build (verified in game: EndOfEvent's field at +16
            // equals the posted event id): pCookie u64, gameObj u32, playingID u32, eventID|syncType u32, ...
            q->valueId = (uint32_t)*(const uint64_t *)p;
            q->gameObj = *(const uint32_t *)(p + 8);
            q->playingId = *(const uint32_t *)(p + 12);
            if (type >= 256 && type <= 16384) {   // music sync: syncType, beat, bar, grid, gridOffset, labelHash
                q->flags = *(const uint32_t *)(p + 16);
                if (inbuf(p, 40)) { q->fvalue = *(const float *)(p + 20); q->i1 = *(const int32_t *)(p + 36); q->i2 = *(const int32_t *)(p + 24); }
            } else {
                q->eventId = *(const uint32_t *)(p + 16);
                if (type == 4 && inbuf(p, 28)) { q->i1 = *(const int32_t *)(p + 20); q->i2 = *(const int32_t *)(p + 24); }
            }
            if (g_level >= 3 && inbuf(p, 64)) memcpy(q->name, p, 62);   // raw payload bytes (RAW telemetry, layout research)
        }
        wt_end(q);
        InterlockedIncrement(&g_cbItems);
        if (next && next <= it) { InterlockedIncrement(&g_cbBad); break; }
        it = next;
    }
    return r;
}

static void add_manual(int i, const char *n, void *h, void **o) {
    g_hooks[WT_NHOOKS_GEN + i].name = n; g_hooks[WT_NHOOKS_GEN + i].hook = h; g_hooks[WT_NHOOKS_GEN + i].orig = o;
}
static int g_nhooks;
static void wt_init_table(void) {
    if (g_nhooks) return;
    add_manual(0, "CSharp_RenderAudio", (void *)h_render, (void **)&o_render);
    add_manual(1, "CSharp_GetIDFromString__SWIG_0", (void *)h_idstr, (void **)&o_idstr);
    add_manual(2, "CSharp_AkCallbackSerializer_Init", (void *)h_cbInit, (void **)&o_cbInit);
    add_manual(3, "CSharp_AkCallbackSerializer_Lock", (void *)h_cbLock, (void **)&o_cbLock);
    g_nhooks = WT_NHOOKS_GEN + 4;
}

// ---------------------------------------------------------------- module detection + resolution
static char g_akPath[MAX_PATH];
static uint32_t g_akSize, g_akStamp, g_akExports;
static volatile LONG g_resolvedAll, g_resolvedHooked, g_enabled = 1;
static FARPROC(WINAPI *o_monoGPA)(HMODULE, LPCSTR);

static void detect(HMODULE m) {
    g_ak = m;
    GetModuleFileNameA(m, g_akPath, MAX_PATH);
    BYTE *b = (BYTE *)m;
    IMAGE_NT_HEADERS *nt = (IMAGE_NT_HEADERS *)(b + ((IMAGE_DOS_HEADER *)b)->e_lfanew);
    g_akSize = nt->OptionalHeader.SizeOfImage;
    g_akStamp = nt->FileHeader.TimeDateStamp;
    IMAGE_DATA_DIRECTORY ed = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_EXPORT];
    if (ed.VirtualAddress) g_akExports = ((IMAGE_EXPORT_DIRECTORY *)(b + ed.VirtualAddress))->NumberOfNames;
    wt_log("[WWISE] AkSoundEngine.dll detected base=%p size=0x%X path=%s timestamp=0x%08X exports=%u",
           (void *)m, g_akSize, g_akPath, g_akStamp, g_akExports);
}

static FARPROC WINAPI hk_monoGPA(HMODULE m, LPCSTR n) {
    FARPROC r = o_monoGPA(m, n);
    if (!r || ((uintptr_t)n >> 16) == 0 || n[0] != 'C' || strncmp(n, "CSharp_", 7) != 0) return r;
    if (m != g_ak) {
        if (g_ak) return r;
        char p[MAX_PATH]; GetModuleFileNameA(m, p, MAX_PATH);
        size_t L = strlen(p);
        if (L < 17 || _stricmp(p + L - 17, "AkSoundEngine.dll") != 0) return r;
        detect(m);
    }
    InterlockedIncrement(&g_resolvedAll);
    if (!g_enabled) return r;
    wt_init_table();
    for (int i = 0; i < g_nhooks; ++i) {
        if (strcmp(g_hooks[i].name, n)) continue;
        if (!*g_hooks[i].orig) *g_hooks[i].orig = (void *)r;
        else if (*g_hooks[i].orig != (void *)r) return r;       // unexpected second address: leave it alone
        if (!g_hooks[i].resolved) { g_hooks[i].resolved = 1; InterlockedIncrement(&g_resolvedHooked); }
        return (FARPROC)g_hooks[i].hook;
    }
    return r;
}

// called by version_proxy.c when mono.dll is first seen
int wt_patch_mono(HMODULE mono, int (*patch_iat)(HMODULE, const char *, const char *, void *, void **)) {
    static int done;
    if (done || !mono) return 0;
    done = 1;
    char flag[MAX_PATH]; GetModuleFileNameA(NULL, flag, MAX_PATH);
    char *s = strrchr(flag, '\\'); if (s) *s = 0;
    strcat(flag, "\\_mod\\wwise_off.flag");
    if (GetFileAttributesA(flag) != INVALID_FILE_ATTRIBUTES) { g_enabled = 0; wt_log("[WWISE] tracer disabled by wwise_off.flag"); }
    int n = patch_iat(mono, "KERNEL32.dll", "GetProcAddress", (void *)hk_monoGPA, (void **)&o_monoGPA);
    if (!o_monoGPA) o_monoGPA = (void *)GetProcAddress;
    wt_log("[WWISE] tracer v%d: patched %d GetProcAddress import(s) in mono.dll; %d entry points watched%s",
           WT_VERSION, n, WT_NHOOKS_GEN + 4, g_enabled ? "" : " (hooks disabled)");
    return n;
}

// ---------------------------------------------------------------- exports for InsideDev
WT_EXPORT int WT_Version(void) { return WT_VERSION; }
WT_EXPORT void WT_SetFrame(int f) { g_frame = f; }
WT_EXPORT int WT_SetLevel(int l) { LONG o = g_level; if (l >= 0 && l <= 3) g_level = l; return o; }

WT_EXPORT int WT_Drain(WtRec *dst, int max) {
    int n = 0;
    uint32_t head = (uint32_t)g_head;
    if (head - g_tail > RING_N) { InterlockedAdd(&g_dropped, (LONG)(head - g_tail - RING_N)); g_tail = head - RING_N; }
    while (n < max && g_tail != head) {
        WtRec *s = &g_ring[g_tail & (RING_N - 1)];
        uint32_t seq = s->seq;
        if (seq == 0) break;                           // writer still filling this slot
        if (seq != g_tail + 1) {                       // overwritten by a newer lap
            if ((int32_t)(seq - (g_tail + 1)) > 0) { InterlockedIncrement(&g_dropped); ++g_tail; continue; }
            break;
        }
        MemoryBarrier();
        memcpy(&dst[n], s, sizeof(WtRec));
        MemoryBarrier();
        if (s->seq != seq) { InterlockedIncrement(&g_dropped); ++g_tail; continue; }
        ++n; ++g_tail;
    }
    return n;
}

// out[0..15]: total, head, tail, dropped, resolvedAll, resolvedHooked, level, akBase, akSize, akStamp,
//             akExports, renderCount, cbItems, cbBad, hookCount, enabled
WT_EXPORT int WT_Stats(int64_t *out, int n) {
    int64_t v[16] = { g_total, g_head, g_tail, g_dropped, g_resolvedAll, g_resolvedHooked, g_level,
                      (int64_t)(uintptr_t)g_ak, g_akSize, g_akStamp, g_akExports, g_render, g_cbItems, g_cbBad,
                      g_nhooks ? g_nhooks : WT_NHOOKS_GEN + 4, g_enabled };
    if (n > 16) n = 16;
    for (int i = 0; i < n; ++i) out[i] = v[i];
    return n;
}
WT_EXPORT int WT_ModulePath(char *buf, int len) { if (len <= 0) return 0; snprintf(buf, len, "%s", g_akPath); return (int)strlen(buf); }

// hook i: name into buf; info[0]=resolved, [1]=calls, [2]=original address, [3]=wrapper address
WT_EXPORT int WT_HookInfo(int i, char *buf, int len, int64_t *info) {
    wt_init_table();
    if (i < 0 || i >= g_nhooks) return 0;
    snprintf(buf, len, "%s", g_hooks[i].name);
    info[0] = g_hooks[i].resolved; info[1] = g_hooks[i].calls;
    info[2] = (int64_t)(uintptr_t)*g_hooks[i].orig; info[3] = (int64_t)(uintptr_t)g_hooks[i].hook;
    return 1;
}
// exported name i of AkSoundEngine.dll with its address (for the API page)
WT_EXPORT int WT_Export(int i, char *buf, int len, int64_t *addr) {
    if (!g_ak) return 0;
    BYTE *b = (BYTE *)g_ak;
    IMAGE_NT_HEADERS *nt = (IMAGE_NT_HEADERS *)(b + ((IMAGE_DOS_HEADER *)b)->e_lfanew);
    IMAGE_DATA_DIRECTORY ed = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_EXPORT];
    if (!ed.VirtualAddress) return 0;
    IMAGE_EXPORT_DIRECTORY *e = (IMAGE_EXPORT_DIRECTORY *)(b + ed.VirtualAddress);
    if (i < 0 || (DWORD)i >= e->NumberOfNames) return 0;
    DWORD *names = (DWORD *)(b + e->AddressOfNames);
    WORD *ords = (WORD *)(b + e->AddressOfNameOrdinals);
    DWORD *funcs = (DWORD *)(b + e->AddressOfFunctions);
    snprintf(buf, len, "%s", (char *)(b + names[i]));
    *addr = (int64_t)(uintptr_t)(b + funcs[ords[i]]);
    return 1;
}

// ---------------------------------------------------------------- native rules API (InsideDev AudioRules mirror)
// kind: 1 nuke, 2 mute, 3 replace (val = new event id), 4 switch (key group, val value), 5 state, 6 rtpc (val = float bits)
WT_EXPORT void WT_RuleEnforce(int on) { g_enforce = on ? 1 : 0; }
WT_EXPORT void WT_RuleClear(void) {
    LONG was = g_enforce; g_enforce = 0; MemoryBarrier();
    memset((void *)g_rNuke, 0, sizeof g_rNuke); memset((void *)g_rMute, 0, sizeof g_rMute); memset((void *)g_rRep, 0, sizeof g_rRep);
    memset((void *)g_rSw, 0, sizeof g_rSw); memset((void *)g_rSt, 0, sizeof g_rSt); memset((void *)g_rRtpc, 0, sizeof g_rRtpc);
    g_ruleCount = 0; MemoryBarrier(); g_enforce = was;
}
WT_EXPORT int WT_RuleAdd(int kind, uint32_t key, uint32_t val) {
    WrSlot *t = kind == 1 ? g_rNuke : kind == 2 ? g_rMute : kind == 3 ? g_rRep : kind == 4 ? g_rSw : kind == 5 ? g_rSt : kind == 6 ? g_rRtpc : 0;
    if (!t || !wr_put(t, key, val)) return 0;
    InterlockedIncrement(&g_ruleCount); return 1;
}
// out: enforce, rules, nuked, muted, replaced, forced
WT_EXPORT int WT_RuleStats(int64_t *out, int n) {
    int64_t v[6] = { g_enforce, g_ruleCount, g_nNuked, g_nMuted, g_nReplaced, g_nForced };
    if (n > 6) n = 6;
    for (int i = 0; i < n; ++i) out[i] = v[i];
    return n;
}
