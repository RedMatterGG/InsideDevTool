// INSIDE version.dll proxy.
//   PUBLIC_LOADER build ("InsideMods loader"): forwards version.dll, boots InsideMods\InsideMods.dll (code mods) and
//     ticks it from the game's own Savegame.Update. Changes no game file; nothing else.
//   default (dev) build: additionally boots _mod\InsideDev.dll, runs the Wwise tracer and, only when
//     _mod\debugger.flag exists, starts Mono's debugger agent. It never writes any game assembly to disk.
// Both builds carry the marker below, which the INSIDE patcher looks for before it installs code mods.
// Loaded by INSIDE.exe (static import of VERSION.dll). Forwards all version.dll
// exports to the real System32 copy, and hooks the exe's GetProcAddress import so
// that it can see when the game's code has loaded (to boot the mod at the right time).
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdio.h>
#include <string.h>
#include <stdint.h>
#include <stdarg.h>
#include <stdlib.h>
#include <wchar.h>

__attribute__((used)) static const char g_marker[] = "InsideModsLoader v1: boots InsideMods\\InsideMods.dll";
// ---------------------------------------------------------------- forwarding
#define NEXP 17
static const char *g_names[NEXP] = {
    "GetFileVersionInfoA", "GetFileVersionInfoByHandle", "GetFileVersionInfoExA",
    "GetFileVersionInfoExW", "GetFileVersionInfoSizeA", "GetFileVersionInfoSizeExA",
    "GetFileVersionInfoSizeExW", "GetFileVersionInfoSizeW", "GetFileVersionInfoW",
    "VerFindFileA", "VerFindFileW", "VerInstallFileA", "VerInstallFileW",
    "VerLanguageNameA", "VerLanguageNameW", "VerQueryValueA", "VerQueryValueW"};
void *g_real[NEXP];

#define STUB(i, n) __asm__(".globl px_" #n "\npx_" #n ":\n\tjmp *(g_real+" #i "*8)(%rip)\n");
STUB(0, GetFileVersionInfoA)
STUB(1, GetFileVersionInfoByHandle)
STUB(2, GetFileVersionInfoExA)
STUB(3, GetFileVersionInfoExW)
STUB(4, GetFileVersionInfoSizeA)
STUB(5, GetFileVersionInfoSizeExA)
STUB(6, GetFileVersionInfoSizeExW)
STUB(7, GetFileVersionInfoSizeW)
STUB(8, GetFileVersionInfoW)
STUB(9, VerFindFileA)
STUB(10, VerFindFileW)
STUB(11, VerInstallFileA)
STUB(12, VerInstallFileW)
STUB(13, VerLanguageNameA)
STUB(14, VerLanguageNameW)
STUB(15, VerQueryValueA)
STUB(16, VerQueryValueW)

// ---------------------------------------------------------------- logging
static CRITICAL_SECTION g_cs;
static FILE *g_log;

static void logf_(const char *fmt, ...) {
    if (!g_log) return;
    EnterCriticalSection(&g_cs);
    SYSTEMTIME t; GetLocalTime(&t);
    fprintf(g_log, "[%02d:%02d:%02d.%03d] ", t.wHour, t.wMinute, t.wSecond, t.wMilliseconds);
    va_list a; va_start(a, fmt); vfprintf(g_log, fmt, a); va_end(a);
    fputc('\n', g_log); fflush(g_log);
    LeaveCriticalSection(&g_cs);
}

void wt_log(const char *fmt, ...) {
    if (!g_log) return;
    char buf[1024];
    va_list a; va_start(a, fmt); vsnprintf(buf, sizeof buf, fmt, a); va_end(a);
    buf[sizeof buf - 1] = 0;
    logf_("%s", buf);
}
int wt_patch_mono(HMODULE mono, int (*patch_iat)(HMODULE, const char *, const char *, void *, void **));

// ---------------------------------------------------------------- paths
// Kept wide: the game may sit in a folder whose name is outside the ANSI code page. Mono and the log get UTF-8.
#define WPATH 2048
static wchar_t g_gameDirW[WPATH], g_modDirW[WPATH];
static int g_pathsOk;   // both folders are known (0: the exe path did not fit, nothing is booted)

// dst = a + "\" + b; 0 (and an empty dst) when it does not fit
static int wjoin(wchar_t *dst, size_t cap, const wchar_t *a, const wchar_t *b) {
    size_t la = wcslen(a), lb = wcslen(b);
    if (!cap || la + lb + 2 > cap) { if (cap) dst[0] = 0; return 0; }
    memmove(dst, a, la * sizeof(wchar_t));
    dst[la] = L'\\';
    memcpy(dst + la + 1, b, (lb + 1) * sizeof(wchar_t));
    return 1;
}
static const char *utf8_(const wchar_t *w, char *buf, int cap) {
    if (cap < 2) return "?";
    if (!WideCharToMultiByte(CP_UTF8, 0, w, -1, buf, cap, NULL, NULL)) { buf[0] = '?'; buf[1] = 0; }
    return buf;
}
// 1 when the file <mod folder>\name exists (flag files); path, when given, receives its full name
int wt_mod_file(const wchar_t *name, wchar_t *path, size_t cap) {
    wchar_t tmp[WPATH];
    if (!path) { path = tmp; cap = WPATH; }
    if (!g_pathsOk || !wjoin(path, cap, g_modDirW, name)) return 0;
    return GetFileAttributesW(path) != INVALID_FILE_ATTRIBUTES;
}

// ---------------------------------------------------------------- mono wrappers
static FARPROC(WINAPI *o_GPA)(HMODULE, LPCSTR);
typedef void *(*fn_dom_asm_open)(void *, const char *);
typedef void *(*fn_load_from_full)(void *, const char *, int *, int);

static fn_load_from_full o_load_from_full;

static volatile LONG g_gameAsmLoaded; // the game's main assembly (Assembly-CSharp) has loaded
static DWORD g_mainThread;
static volatile LONG g_booted;
static volatile LONG g_invMain, g_invOther;   // mono_runtime_invoke calls seen between the game assembly load and the boot
static void *g_firstpassImg;                  // image of Assembly-CSharp-firstpass (holds Savegame)

// Says in the log why nothing works when the managed boot never happens.
static DWORD WINAPI boot_watch(LPVOID p) {
    (void)p;
    Sleep(20000);
    if (!g_booted)
        logf_("mod boot did not run: 20 s after the game assembly loaded, no Start / Update / LateUpdate / FixedUpdate went "
              "through mono_runtime_invoke on the main thread (calls seen: %ld on the main thread, %ld on other threads%s)",
              (long)g_invMain, (long)g_invOther, g_invMain + g_invOther == 0 ? "; the hook on mono_runtime_invoke was never reached" : "");
    return 0;
}

static void *hk_load_from_full(void *img, const char *f, int *s, int ro) {
    void *r = o_load_from_full(img, f, s, ro);
    // arm the mod boot when the game's main assembly loads (identified by its assembly name, not by file)
    typedef const char *(*fn_image_name)(void *);
    static fn_image_name image_name;
    if (!image_name) { image_name = (fn_image_name)(void *)o_GPA(GetModuleHandleA("mono.dll"), "mono_image_get_name"); if (!image_name) logf_("mono_image_get_name missing: mod boot cannot be armed"); }
    const char *an = (r && img && image_name) ? image_name(img) : NULL;
    if (r && an && strcmp(an, "Assembly-CSharp-firstpass") == 0) g_firstpassImg = img;
    if (r && an && strcmp(an, "Assembly-CSharp") == 0) {
        g_mainThread = GetCurrentThreadId();
        if (InterlockedExchange(&g_gameAsmLoaded, 1) == 0) {
            HANDLE th = CreateThread(NULL, 0, boot_watch, NULL, 0, NULL);
            if (th) CloseHandle(th);
        }
        logf_("game assembly loaded on thread %lu; mod boot armed", g_mainThread);
    }
    return r;
}

// ---------------------------------------------------------------- mod boot via mono_runtime_invoke
typedef void *(*fn_runtime_invoke)(void *method, void *obj, void **params, void **exc);
typedef void *(*fn_domain_get)(void);
typedef void *(*fn_get_image)(void *);
typedef void *(*fn_class_from_name)(void *, const char *, const char *);
typedef void *(*fn_get_method)(void *, const char *, int);
typedef const char *(*fn_method_name)(void *);
typedef void *(*fn_obj_to_string)(void *, void **);
typedef char *(*fn_string_to_utf8)(void *);

static fn_runtime_invoke o_runtime_invoke;
static int g_invokeLogged;
static void *g_sgUpdate, *g_sgGui;               // Savegame.Update / Savegame.OnGUI
static int g_sgDone, g_sgWait;                   // g_sgDone: the lookup by method name in the invoke hook is over
typedef void *(*fn_method_class)(void *);
typedef const char *(*fn_class_name)(void *);
static fn_method_class p_method_get_class;
static fn_class_name p_class_get_name;

// managed modules booted by this loader: Init() once, Tick() every frame (from Savegame.Update), Gui() from OnGUI
typedef struct { const char *rel, *ns, *cls; void *tick, *gui; int ok; } Module;
static Module g_mods[] = {
#ifndef PUBLIC_LOADER
    { "_mod\\InsideDev.dll", "InsideDev", "Boot", 0, 0, 0 },
#endif
    { "InsideMods\\InsideMods.dll", "InsideMods", "Runtime", 0, 0, 0 },
};
#define NMODS ((int)(sizeof g_mods / sizeof g_mods[0]))
static int g_anyTick;

static void boot_one(Module *md) {
    HMODULE mono = GetModuleHandleA("mono.dll");
    if (!mono) { logf_("boot: mono.dll not found"); return; }
#define MF(t, n) t n = (t)(void *)o_GPA(mono, #n); if (!n) { logf_("boot: missing " #n); return; }
    MF(fn_domain_get, mono_domain_get)
    MF(fn_dom_asm_open, mono_domain_assembly_open)
    MF(fn_get_image, mono_assembly_get_image)
    MF(fn_class_from_name, mono_class_from_name)
    MF(fn_get_method, mono_class_get_method_from_name)
#undef MF
    fn_obj_to_string to_str = (fn_obj_to_string)(void *)o_GPA(mono, "mono_object_to_string");
    fn_string_to_utf8 to_utf8 = (fn_string_to_utf8)(void *)o_GPA(mono, "mono_string_to_utf8");
    p_method_get_class = (fn_method_class)(void *)o_GPA(mono, "mono_method_get_class");
    p_class_get_name = (fn_class_name)(void *)o_GPA(mono, "mono_class_get_name");

    wchar_t wrel[64], wpath[WPATH];
    char path[WPATH * 3];
    size_t ri = 0;
    for (; md->rel[ri] && ri + 1 < sizeof wrel / sizeof wrel[0]; ++ri) wrel[ri] = (unsigned char)md->rel[ri];
    wrel[ri] = 0;
    if (!g_pathsOk || !wjoin(wpath, WPATH, g_gameDirW, wrel)) { logf_("boot: the path of %s is too long, skipping", md->rel); return; }
    if (GetFileAttributesW(wpath) == INVALID_FILE_ATTRIBUTES) { logf_("boot: %s not present, skipping", md->rel); return; }
    if (!WideCharToMultiByte(CP_UTF8, 0, wpath, -1, path, sizeof path, NULL, NULL)) { logf_("boot: the path of %s cannot be converted, skipping", md->rel); return; }
    void *dom = mono_domain_get();
    void *as = mono_domain_assembly_open(dom, path);
    if (!as) {
        // a Mono build that opens files with the ANSI API fails on a UTF-8 name with non-ASCII characters: try the
        // code-page form once (only when the name survives that conversion unchanged)
        int ascii = 1;
        for (const char *c = path; *c; ++c) if ((unsigned char)*c >= 0x80) { ascii = 0; break; }
        char apath[WPATH];
        BOOL lossy = FALSE;
        if (!ascii && WideCharToMultiByte(CP_ACP, WC_NO_BEST_FIT_CHARS, wpath, -1, apath, sizeof apath, NULL, &lossy) && !lossy) {
            as = mono_domain_assembly_open(dom, apath);
            if (as) logf_("boot: %s opened through the code-page form of its path", md->rel);
        }
    }
    if (!as) { logf_("boot: failed to open %s", path); return; }
    void *img = mono_assembly_get_image(as);
    void *klass = mono_class_from_name(img, md->ns, md->cls);
    if (!klass) { logf_("boot: class %s.%s not found", md->ns, md->cls); return; }
    void *m = mono_class_get_method_from_name(klass, "Init", 0);
    if (!m) { logf_("boot: %s.%s.Init() not found", md->ns, md->cls); return; }
    md->tick = mono_class_get_method_from_name(klass, "Tick", 0);
    md->gui = mono_class_get_method_from_name(klass, "Gui", 0);
    if (md->tick) g_anyTick = 1;
    void *exc = NULL;
    o_runtime_invoke(m, NULL, NULL, &exc);
    if (exc) {
        const char *msg = "(unknown)";
        if (to_str && to_utf8) { void *e2 = NULL; void *str = to_str(exc, &e2); if (str && !e2) msg = to_utf8(str); }
        logf_("boot: %s.%s.Init() threw: %s", md->ns, md->cls, msg);
    } else {
        md->ok = 1;
        logf_("boot: %s.%s.Init() OK (Tick=%p Gui=%p)", md->ns, md->cls, md->tick, md->gui);
    }
}

// Savegame.Update / OnGUI straight from the class, so the invoke hook does not have to compare method names.
static void resolve_driver(void) {
    HMODULE mono = GetModuleHandleA("mono.dll");
    if (!mono || !g_firstpassImg || g_sgDone) return;
    fn_class_from_name class_from_name = (fn_class_from_name)(void *)o_GPA(mono, "mono_class_from_name");
    fn_get_method get_method = (fn_get_method)(void *)o_GPA(mono, "mono_class_get_method_from_name");
    if (!class_from_name || !get_method) return;
    void *klass = class_from_name(g_firstpassImg, "", "Savegame");
    if (!klass) { logf_("driver: class Savegame not found in Assembly-CSharp-firstpass; looking the methods up by name"); return; }
    void *upd = get_method(klass, "Update", 0);
    if (!upd) { logf_("driver: Savegame.Update not found; looking the methods up by name"); return; }
    g_sgUpdate = upd;
    g_sgGui = get_method(klass, "OnGUI", 0);
    g_sgDone = 1;
    logf_("driver: Savegame.Update = %p, Savegame.OnGUI = %p", g_sgUpdate, g_sgGui);
}

static void boot_mod(void) {
    int ok = 0;
    for (int i = 0; i < NMODS; ++i) { boot_one(&g_mods[i]); ok += g_mods[i].ok; }
    if (!ok) logf_("mod boot did not run: no module started (see the boot lines above)");
    if (g_anyTick) resolve_driver();
}

static fn_method_name g_getName;

static void *hk_runtime_invoke(void *method, void *obj, void **params, void **exc) {
    if (g_booted && g_anyTick && GetCurrentThreadId() == g_mainThread) {
        if (!g_sgDone) {
            // only when the class lookup at boot failed. Over once Update is known and OnGUI had its chance to show up:
            // the engine never dispatches OnGUI in this game, and waiting for it for ever would tax every script call
            if (!g_getName) g_getName = (fn_method_name)(void *)o_GPA(GetModuleHandleA("mono.dll"), "mono_method_get_name");
            const char *nm = g_getName ? g_getName(method) : NULL;
            if (nm && (!strcmp(nm, "Update") || !strcmp(nm, "OnGUI")) && p_method_get_class && p_class_get_name) {
                const char *cn = p_class_get_name(p_method_get_class(method));
                if (cn && !strcmp(cn, "Savegame")) {
                    if (nm[0] == 'U' && !g_sgUpdate) { g_sgUpdate = method; logf_("driver: Savegame.Update = %p", method); }
                    if (nm[0] == 'O' && !g_sgGui) { g_sgGui = method; logf_("driver: Savegame.OnGUI = %p", method); }
                }
            }
            if (g_sgUpdate && (g_sgGui || ++g_sgWait > 200000)) g_sgDone = 1;
        }
        if (g_sgGui && method == g_sgGui) {
            void *r = o_runtime_invoke(method, obj, params, exc);
            for (int i = 0; i < NMODS; ++i) if (g_mods[i].ok && g_mods[i].gui) { void *e2 = NULL; o_runtime_invoke(g_mods[i].gui, NULL, NULL, &e2); }
            return r;
        }
        if (g_sgUpdate && method == g_sgUpdate) {
            for (int i = 0; i < NMODS; ++i) if (g_mods[i].ok && g_mods[i].tick) { void *e2 = NULL; o_runtime_invoke(g_mods[i].tick, NULL, NULL, &e2); }
        }
    }
    if (g_gameAsmLoaded && !g_booted && GetCurrentThreadId() != g_mainThread) InterlockedIncrement(&g_invOther);
    if (g_gameAsmLoaded && !g_booted && GetCurrentThreadId() == g_mainThread) {
        InterlockedIncrement(&g_invMain);
        static fn_method_name get_name;
        if (!get_name) get_name = (fn_method_name)(void *)o_GPA(GetModuleHandleA("mono.dll"), "mono_method_get_name");
        const char *nm = get_name ? get_name(method) : "?";
        if (g_invokeLogged < 25) { ++g_invokeLogged; logf_("invoke: %s", nm ? nm : "?"); }
        if (nm && (!strcmp(nm, "Start") || !strcmp(nm, "Update") || !strcmp(nm, "LateUpdate") || !strcmp(nm, "FixedUpdate"))) {
            if (InterlockedExchange(&g_booted, 1) == 0) {
                logf_("boot: triggered by %s", nm);
                boot_mod();
            }
        }
    }
    return o_runtime_invoke(method, obj, params, exc);
}


// ---------------------------------------------------------------- Mono soft debugger (script debugging)
// INSIDE ships a release player, but its mono.dll still contains Mono's debugger agent. Unity's development players
// enable it by calling mono_jit_parse_options("--debugger-agent=...") and mono_debug_init() before the JIT starts;
// we do the same when Unity resolves mono_jit_init_version, but only when the user asked for it: the agent has no
// authentication and makes the JIT emit debuggable code. Off by default. _mod\debugger.flag turns it on; the first line
// of that file may hold the port (1024-65535, default 55555). _mod\nodebugger.flag keeps it off whatever else exists.
// Attach with dnSpy: Debug > Start Debugging > Debug engine "Unity (Connect)", IP 127.0.0.1, the port.
typedef void *(*fn_jit_init_version)(const char *, const char *);
typedef void *(*fn_jit_init)(const char *);
typedef void (*fn_jit_parse_options)(int, char **);
typedef void (*fn_debug_init)(int);
static fn_jit_init_version o_jit_init_version;
static fn_jit_init o_jit_init;
#define DEBUGGER_PORT 55555
static int g_dbgOn, g_dbgPort;

// The port on the first line of the flag file; 0 when the line is empty, -1 when it is not a port. Tolerates a byte
// order mark and UTF-16 text (what "echo 5000 > debugger.flag" writes in PowerShell).
static int flag_port(const wchar_t *file) {
    unsigned char raw[64];
    FILE *f = _wfopen(file, L"rb");
    if (!f) return 0;
    size_t n = fread(raw, 1, sizeof raw, f);
    fclose(f);
    long v = 0;
    int digits = 0;
    for (size_t i = 0; i < n; ++i) {
        unsigned char c = raw[i];
        if (c == 0) continue;
        if (c >= '0' && c <= '9') { if (++digits > 5) return -1; v = v * 10 + (c - '0'); continue; }
        if (c == '\r' || c == '\n') break;
        if (c == ' ' || c == '\t') { if (digits) break; continue; }
        if (!digits && (c == 0xEF || c == 0xBB || c == 0xBF || c == 0xFF || c == 0xFE)) continue;
        return -1;
    }
    if (!digits) return 0;
    return v >= 1024 && v <= 65535 ? (int)v : -1;
}

static void enable_debugger(void) {
    static int decided;
    if (decided) return;
    decided = 1;
    wchar_t flag[WPATH];
    if (wt_mod_file(L"nodebugger.flag", NULL, 0)) { logf_("debugger: off (nodebugger.flag)"); return; }
    if (!wt_mod_file(L"debugger.flag", flag, WPATH)) { logf_("debugger: off (create _mod\\debugger.flag to start Mono's debugger agent)"); return; }
    int port = flag_port(flag);
    if (port < 0) logf_("debugger: the first line of debugger.flag is not a port (1024-65535); using %d", DEBUGGER_PORT);
    if (port <= 0) port = DEBUGGER_PORT;
    HMODULE mono = GetModuleHandleA("mono.dll");
    fn_jit_parse_options parse = mono ? (fn_jit_parse_options)(void *)o_GPA(mono, "mono_jit_parse_options") : NULL;
    fn_debug_init dinit = mono ? (fn_debug_init)(void *)o_GPA(mono, "mono_debug_init") : NULL;
    if (!parse || !dinit) { logf_("debugger: mono exports missing (parse=%p debug_init=%p)", parse, dinit); return; }
    static char opt[128];
    snprintf(opt, sizeof opt, "--debugger-agent=transport=dt_socket,embedding=1,server=y,suspend=n,defer=y,address=127.0.0.1:%d", port);
    char *argv[1] = { opt };
    parse(1, argv);
    dinit(1 /* MONO_DEBUG_FORMAT_MONO */);
    g_dbgOn = 1; g_dbgPort = port;
    logf_("debugger: agent listening on 127.0.0.1:%d (debugger.flag; attach with dnSpy 'Unity (Connect)')", port);
}
static void *hk_jit_init_version(const char *domain, const char *ver) { enable_debugger(); return o_jit_init_version(domain, ver); }
static void *hk_jit_init(const char *file) { enable_debugger(); return o_jit_init(file); }

// ---------------------------------------------------------------- GetProcAddress hook

static int patch_iat(HMODULE mod, const char *dll, const char *func, void *hook, void **orig);

static FARPROC WINAPI hk_GPA(HMODULE m, LPCSTR n) {
    FARPROC r = o_GPA(m, n);
    if (!r || ((uintptr_t)n >> 16) == 0 || strncmp(n, "mono_", 5) != 0) return r;
#ifndef PUBLIC_LOADER
    wt_patch_mono(m, patch_iat);   // WwiseTracer: watch mono.dll's own GetProcAddress (P/Invoke resolution)
#endif
#define HOOK(sym, orig, hk) \
    if (!strcmp(n, sym)) { orig = (void *)r; logf_("hooked %s", sym); return (FARPROC)(void *)hk; }
#ifndef PUBLIC_LOADER
    HOOK("mono_jit_init_version", o_jit_init_version, hk_jit_init_version)
    HOOK("mono_jit_init", o_jit_init, hk_jit_init)
#endif
    HOOK("mono_assembly_load_from_full", o_load_from_full, hk_load_from_full)
    HOOK("mono_runtime_invoke", o_runtime_invoke, hk_runtime_invoke)
#undef HOOK
    return r;
}

static int patch_iat(HMODULE mod, const char *dll, const char *func, void *hook, void **orig) {
    BYTE *base = (BYTE *)mod;
    IMAGE_NT_HEADERS *nt = (IMAGE_NT_HEADERS *)(base + ((IMAGE_DOS_HEADER *)base)->e_lfanew);
    IMAGE_DATA_DIRECTORY dir = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT];
    if (!dir.VirtualAddress) return 0;
    int n = 0;
    for (IMAGE_IMPORT_DESCRIPTOR *d = (void *)(base + dir.VirtualAddress); d->Name; ++d) {
        if (_stricmp((char *)(base + d->Name), dll)) continue;
        IMAGE_THUNK_DATA *oft = (void *)(base + (d->OriginalFirstThunk ? d->OriginalFirstThunk : d->FirstThunk));
        IMAGE_THUNK_DATA *ft = (void *)(base + d->FirstThunk);
        for (; oft->u1.AddressOfData; ++oft, ++ft) {
            if (IMAGE_SNAP_BY_ORDINAL(oft->u1.Ordinal)) continue;
            IMAGE_IMPORT_BY_NAME *ibn = (void *)(base + oft->u1.AddressOfData);
            if (strcmp((char *)ibn->Name, func)) continue;
            DWORD old;
            VirtualProtect(&ft->u1.Function, sizeof(void *), PAGE_READWRITE, &old);
            if (orig && !*orig) *orig = (void *)ft->u1.Function;
            ft->u1.Function = (ULONG_PTR)hook;
            VirtualProtect(&ft->u1.Function, sizeof(void *), old, &old);
            ++n;
        }
    }
    return n;
}

// ---------------------------------------------------------------- entry
BOOL WINAPI DllMain(HINSTANCE h, DWORD reason, LPVOID r) {
    (void)r;
    if (reason != DLL_PROCESS_ATTACH) return TRUE;
    DisableThreadLibraryCalls(h);
    InitializeCriticalSection(&g_cs);

    wchar_t sys[MAX_PATH + 16];
    UINT sn = GetSystemDirectoryW(sys, MAX_PATH);
    HMODULE real = NULL;
    if (sn > 0 && sn < MAX_PATH) { wcscpy(sys + sn, L"\\version.dll"); real = LoadLibraryW(sys); }
    for (int i = 0; i < NEXP; ++i) g_real[i] = real ? (void *)GetProcAddress(real, g_names[i]) : NULL;

    DWORD en = GetModuleFileNameW(NULL, g_gameDirW, WPATH);
    wchar_t *slash = en > 0 && en < WPATH ? wcsrchr(g_gameDirW, L'\\') : NULL;
    if (slash) *slash = 0; else g_gameDirW[0] = 0;
    wchar_t logp[WPATH];
#ifdef PUBLIC_LOADER
    // log only when the InsideMods folder exists (the loader itself never creates folders)
    g_pathsOk = slash && wjoin(g_modDirW, WPATH, g_gameDirW, L"InsideMods");
    if (g_pathsOk && GetFileAttributesW(g_modDirW) != INVALID_FILE_ATTRIBUTES && wjoin(logp, WPATH, g_modDirW, L"loader.log")) g_log = _wfopen(logp, L"w");
#else
    g_pathsOk = slash && wjoin(g_modDirW, WPATH, g_gameDirW, L"_mod");
    if (g_pathsOk) {
        CreateDirectoryW(g_modDirW, NULL);
        if (wjoin(logp, WPATH, g_modDirW, L"hook.log")) g_log = _wfopen(logp, L"w");
    }
#endif
    logf_("proxy loaded (%s); real version.dll=%p", g_marker, real);
    int n = patch_iat(GetModuleHandleA(NULL), "KERNEL32.dll", "GetProcAddress", (void *)hk_GPA, (void **)&o_GPA);
    logf_("patched %d GetProcAddress import(s) in exe", n);
    if (!o_GPA) o_GPA = (void *)GetProcAddress;
    char dir8[WPATH * 3];
    logf_("game folder: %s", utf8_(g_gameDirW, dir8, sizeof dir8));
    return TRUE;
}

// ---------------------------------------------------------------- state for InsideDev (doctor)
// out[0..7]: info version (1), public loader build, managed boot ran, modules started, debugger agent on, debugger port,
//            runtime_invoke calls seen on the main thread before the boot, Savegame.Update known
__declspec(dllexport) int WT_LoaderInfo(int64_t *out, int n) {
    int ok = 0;
    for (int i = 0; i < NMODS; ++i) ok += g_mods[i].ok;
#ifdef PUBLIC_LOADER
    int pub = 1;
#else
    int pub = 0;
#endif
    int64_t v[8] = { 1, pub, g_booted, ok, g_dbgOn, g_dbgPort, g_invMain, g_sgUpdate != NULL };
    if (n > 8) n = 8;
    for (int i = 0; i < n; ++i) out[i] = v[i];
    return n;
}
