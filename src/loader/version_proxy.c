// INSIDE version.dll proxy.
//   PUBLIC_LOADER build ("InsideMods loader"): forwards version.dll, boots InsideMods\InsideMods.dll (code mods) and
//     ticks it from the game's own Savegame.Update. Changes no game file; nothing else.
//   default (dev) build: additionally boots _mod\InsideDev.dll, runs the Wwise tracer and enables Mono's debugger
//     agent. It never writes any game assembly to disk.
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
    logf_("%s", buf);
}
int wt_patch_mono(HMODULE mono, int (*patch_iat)(HMODULE, const char *, const char *, void *, void **));

// ---------------------------------------------------------------- mono wrappers
static FARPROC(WINAPI *o_GPA)(HMODULE, LPCSTR);
typedef void *(*fn_with_name)(char *, uint32_t, int, int *, int, const char *);
typedef void *(*fn_full)(char *, uint32_t, int, int *, int);
typedef void *(*fn_plain)(char *, uint32_t, int, int *);
typedef void *(*fn_asm_open)(const char *, int *);
typedef void *(*fn_dom_asm_open)(void *, const char *);
typedef void *(*fn_load_from_full)(void *, const char *, int *, int);

static fn_with_name o_with_name;
static fn_full o_full;
static fn_plain o_plain;
static fn_asm_open o_asm_open;
static fn_dom_asm_open o_dom_asm_open;
static fn_load_from_full o_load_from_full;

static volatile LONG g_gameAsmLoaded; // Assembly-CSharp (BackgroundData.asset) loaded
static DWORD g_mainThread;
static volatile LONG g_booted;
static char g_modDir[MAX_PATH];

static int ends_with_ci(const char *s, const char *suf) {
    size_t a = strlen(s), b = strlen(suf);
    return a >= b && _stricmp(s + a - b, suf) == 0;
}

static void *hk_with_name(char *d, uint32_t l, int c, int *s, int r, const char *n) {
    logf_("image: %s (%u bytes)", n ? n : "(null)", l);
    return o_with_name(d, l, c, s, r, n);
}
static void *hk_full(char *d, uint32_t l, int c, int *s, int r) {
    logf_("image (full): %u bytes", l);
    return o_full(d, l, c, s, r);
}
static void *hk_plain(char *d, uint32_t l, int c, int *s) {
    logf_("image: %u bytes", l);
    return o_plain(d, l, c, s);
}
static void *hk_asm_open(const char *f, int *s) {
    void *r = o_asm_open(f, s);
    logf_("mono_assembly_open(%s) -> %p status=%d", f ? f : "(null)", r, s ? *s : -1);
    return r;
}
static void *hk_dom_asm_open(void *d, const char *f) {
    void *r = o_dom_asm_open(d, f);
    logf_("mono_domain_assembly_open(%s) -> %p", f ? f : "(null)", r);
    return r;
}
static void *hk_load_from_full(void *img, const char *f, int *s, int ro) {
    void *r = o_load_from_full(img, f, s, ro);
    logf_("mono_assembly_load_from_full(img=%p, %s) -> %p", img, f ? f : "(null)", r);
    if (r && f && ends_with_ci(f, "BackgroundData.asset")) {
        g_mainThread = GetCurrentThreadId();
        InterlockedExchange(&g_gameAsmLoaded, 1);
        logf_("Assembly-CSharp loaded on thread %lu; mod boot armed", g_mainThread);
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
static void *g_sgUpdate, *g_sgGui;               // Savegame.Update / Savegame.OnGUI (resolved lazily)
typedef void *(*fn_method_class)(void *);
typedef const char *(*fn_class_name)(void *);
static fn_method_class p_method_get_class;
static fn_class_name p_class_get_name;
static char g_gameDir[MAX_PATH];

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

    char path[MAX_PATH * 2];
    snprintf(path, sizeof path, "%s\\%s", g_gameDir, md->rel);
    if (GetFileAttributesA(path) == INVALID_FILE_ATTRIBUTES) { logf_("boot: %s not present, skipping", md->rel); return; }
    void *dom = mono_domain_get();
    void *as = (o_dom_asm_open ? o_dom_asm_open : mono_domain_assembly_open)(dom, path);
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

static void boot_mod(void) { for (int i = 0; i < NMODS; ++i) boot_one(&g_mods[i]); }

static fn_method_name g_getName;

static void *hk_runtime_invoke(void *method, void *obj, void **params, void **exc) {
    if (g_booted && g_anyTick && GetCurrentThreadId() == g_mainThread) {
        if (!g_sgUpdate || !g_sgGui) {
            if (!g_getName) g_getName = (fn_method_name)(void *)o_GPA(GetModuleHandleA("mono.dll"), "mono_method_get_name");
            const char *nm = g_getName ? g_getName(method) : NULL;
            if (nm && (!strcmp(nm, "Update") || !strcmp(nm, "OnGUI")) && p_method_get_class && p_class_get_name) {
                const char *cn = p_class_get_name(p_method_get_class(method));
                if (cn && !strcmp(cn, "Savegame")) {
                    if (nm[0] == 'U' && !g_sgUpdate) { g_sgUpdate = method; logf_("driver: Savegame.Update = %p", method); }
                    if (nm[0] == 'O' && !g_sgGui) { g_sgGui = method; logf_("driver: Savegame.OnGUI = %p", method); }
                }
            }
        }
        if (method == g_sgGui) {
            void *r = o_runtime_invoke(method, obj, params, exc);
            for (int i = 0; i < NMODS; ++i) if (g_mods[i].ok && g_mods[i].gui) { void *e2 = NULL; o_runtime_invoke(g_mods[i].gui, NULL, NULL, &e2); }
            return r;
        }
        if (method == g_sgUpdate) {
            for (int i = 0; i < NMODS; ++i) if (g_mods[i].ok && g_mods[i].tick) { void *e2 = NULL; o_runtime_invoke(g_mods[i].tick, NULL, NULL, &e2); }
        }
    }
    if (g_gameAsmLoaded && !g_booted && GetCurrentThreadId() == g_mainThread) {
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
// we do the same when Unity resolves mono_jit_init_version. On by default; _mod\nodebugger.flag turns it off.
// Attach with dnSpy: Debug > Start Debugging > Debug engine "Unity (Connect)", IP 127.0.0.1, port 55555.
typedef void *(*fn_jit_init_version)(const char *, const char *);
typedef void *(*fn_jit_init)(const char *);
typedef void (*fn_jit_parse_options)(int, char **);
typedef void (*fn_debug_init)(int);
static fn_jit_init_version o_jit_init_version;
static fn_jit_init o_jit_init;
static void enable_debugger(void) {
    char flag[MAX_PATH + 32];
    snprintf(flag, sizeof flag, "%s\\nodebugger.flag", g_modDir);
    if (GetFileAttributesA(flag) != INVALID_FILE_ATTRIBUTES) { logf_("debugger: off (nodebugger.flag)"); return; }
    HMODULE mono = GetModuleHandleA("mono.dll");
    fn_jit_parse_options parse = mono ? (fn_jit_parse_options)(void *)o_GPA(mono, "mono_jit_parse_options") : NULL;
    fn_debug_init dinit = mono ? (fn_debug_init)(void *)o_GPA(mono, "mono_debug_init") : NULL;
    if (!parse || !dinit) { logf_("debugger: mono exports missing (parse=%p debug_init=%p)", parse, dinit); return; }
    static char opt[] = "--debugger-agent=transport=dt_socket,embedding=1,server=y,suspend=n,defer=y,address=127.0.0.1:55555";
    char *argv[1] = { opt };
    parse(1, argv);
    dinit(1 /* MONO_DEBUG_FORMAT_MONO */);
    logf_("debugger: agent listening on 127.0.0.1:55555 (attach with dnSpy 'Unity (Connect)')");
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
    HOOK("mono_image_open_from_data_with_name", o_with_name, hk_with_name)
    HOOK("mono_image_open_from_data_full", o_full, hk_full)
    HOOK("mono_image_open_from_data", o_plain, hk_plain)
    HOOK("mono_assembly_open", o_asm_open, hk_asm_open)
    HOOK("mono_jit_init_version", o_jit_init_version, hk_jit_init_version)
    HOOK("mono_jit_init", o_jit_init, hk_jit_init)
#endif
    HOOK("mono_domain_assembly_open", o_dom_asm_open, hk_dom_asm_open)
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

    char sys[MAX_PATH];
    GetSystemDirectoryA(sys, MAX_PATH);
    strcat(sys, "\\version.dll");
    HMODULE real = LoadLibraryA(sys);
    for (int i = 0; i < NEXP; ++i) g_real[i] = real ? (void *)GetProcAddress(real, g_names[i]) : NULL;

    char exe[MAX_PATH];
    GetModuleFileNameA(NULL, exe, MAX_PATH);
    char *slash = strrchr(exe, '\\');
    if (slash) *slash = 0;
    snprintf(g_gameDir, sizeof g_gameDir, "%s", exe);
#ifdef PUBLIC_LOADER
    // log only when the InsideMods folder exists (the loader itself never creates folders)
    snprintf(g_modDir, sizeof g_modDir, "%s\\InsideMods", exe);
    char logp[MAX_PATH + 16];
    snprintf(logp, sizeof logp, "%s\\loader.log", g_modDir);
    if (GetFileAttributesA(g_modDir) != INVALID_FILE_ATTRIBUTES) g_log = fopen(logp, "w");
#else
    snprintf(g_modDir, sizeof g_modDir, "%s\\_mod", exe);
    CreateDirectoryA(g_modDir, NULL);
    char logp[MAX_PATH + 16];
    snprintf(logp, sizeof logp, "%s\\hook.log", g_modDir);
    g_log = fopen(logp, "w");
#endif
    logf_("proxy loaded (%s); real version.dll=%p", g_marker, real);
    int n = patch_iat(GetModuleHandleA(NULL), "KERNEL32.dll", "GetProcAddress", (void *)hk_GPA, (void **)&o_GPA);
    logf_("patched %d GetProcAddress import(s) in exe", n);
    if (!o_GPA) o_GPA = (void *)GetProcAddress;
    return TRUE;
}
