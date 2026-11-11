-- InsideDev link for Cheat Engine (Lua). Loaded by InsideDev.CT, or copy this file into the "autorun" folder of the
-- Cheat Engine program folder to have it every time Cheat Engine starts.
--   * attaches to INSIDE.exe if nothing is attached
--   * group "InsideDev selection" in the address list = the object selected in InsideDev (updates on every selection)
--   * right-click menus:
--       address list:   "InsideDev: what is this?"            (the address of the selected record)
--       memory viewer:  "InsideDev: what is this?" / "InsideDev: which method?"   (selected byte / instruction)
--       Table menu:     "InsideDev: what is at address..." / "InsideDev: which method at address..." /
--                       "InsideDev: re-send selection" / "InsideDev: link status"
-- Talks to the game through files in <INSIDE>\_mod\ce\. The file .link there switches the link on in the game; this
-- script rewrites it every few seconds while Cheat Engine is attached to INSIDE (a heartbeat) and removes it when Cheat
-- Engine moves to another process. While it is attached to another program the script does nothing.
-- Addresses are only valid until the object is destroyed, the level reloads or the game restarts.

InsideDev = InsideDev or {}
local D = InsideDev
D.dir = nil; D.pid = nil; D.noGame = false; D.failed = nil; D.complained = false
D.stamp = nil; D.waiting = nil; D.beat = 0
D.askId = D.askId or (os.time() % 1000000)

local HEARTBEAT, RETRY, ANSWER_TIMEOUT = 3, 10, 5
local okLfs, lfs = pcall(require, "lfs")
if not okLfs or type(lfs) ~= "table" then lfs = nil end

local function log(s) print("[InsideDev] " .. tostring(s)) end

-- folder of the attached INSIDE.exe; the second result says whether the modules could be listed at all
local function findDir()
  if getOpenedProcessID() == 0 then return nil, false end
  local ok, mods = pcall(enumModules)
  if not ok or mods == nil or #mods == 0 then return nil, false end
  for _, m in ipairs(mods) do
    if m.Name and m.Name:lower() == "inside.exe" and m.PathToFile then
      local base = m.PathToFile:match("^(.*)[\\/][^\\/]+$")
      if base then return base .. "\\_mod\\ce", true end
    end
  end
  return nil, true
end

local function writeLink()
  local f = io.open(D.dir .. "\\.link", "w")
  if not f then return false end
  f:write("cheat engine link " .. os.time() .. "\n"); f:close()
  D.beat = os.clock()
  return true
end

local function readAll(path)
  local f = io.open(path, "r"); if not f then return nil end
  local s = f:read("*a"); f:close(); return s
end

local ask

local function ensureDir()
  if D.dir then return D.dir end
  if D.noGame then return nil end
  if D.failed and os.clock() - D.failed < RETRY then return nil end
  local dir, listed = findDir()
  if not dir then
    if listed then D.noGame = true else D.failed = os.clock() end
    return nil
  end
  if lfs and lfs.mkdir then pcall(lfs.mkdir, dir) end
  D.dir = dir
  local linked = writeLink()
  if not linked and not lfs and D.madeFor ~= D.pid then
    -- a Cheat Engine without the lfs library: make the folder through the shell, once per attached process, and only
    -- inside an existing _mod folder (a game copy without InsideDev gets nothing)
    D.madeFor = D.pid
    local parent = dir:match("^(.*)\\[^\\]+$")
    if parent and os.rename(parent, parent) then
      pcall(os.execute, 'mkdir "' .. dir .. '" 2>nul')
      linked = writeLink()
    end
  end
  if not linked then
    D.dir = nil; D.failed = os.clock()
    if not D.complained then
      D.complained = true
      log("cannot write to " .. dir .. " (InsideDev must be installed in that game folder: its _mod folder has to exist); trying again every " .. RETRY .. " s")
    end
    return nil
  end
  D.failed = nil; D.complained = false; D.stamp = nil
  os.remove(dir .. "\\selection.txt")
  log("linked: " .. dir)
  ask("refresh", true)
  return D.dir
end

local types = { ["Float"] = vtSingle, ["4 Bytes"] = vtDword, ["8 Bytes"] = vtQword, ["Double"] = vtDouble, ["2 Bytes"] = vtWord, ["Byte"] = vtByte }

local function findGroup()
  local al = getAddressList()
  for i = 0, al.Count - 1 do
    local r = al[i]
    if r.Description:sub(1, 19) == "InsideDev selection" and r.Parent == nil then return r end
  end
  return nil
end

local function group()
  local g = findGroup()
  if g then return g end
  g = getAddressList().createMemoryRecord()
  g.Description = "InsideDev selection"
  g.IsGroupHeader = true
  return g
end

local function empty(g)
  while g.Count > 0 do
    local c = g.Child[g.Count - 1]
    empty(c)
    c.delete()
  end
end

local function clearGroup(text)
  local g = findGroup()
  if not g then return end
  empty(g)
  g.Description = "InsideDev selection: " .. text
end

local function rebuild(lines)
  local g = group()
  empty(g)
  local al = getAddressList()
  local comp = nil
  for _, l in ipairs(lines) do
    local p = {}
    for x in (l .. "|"):gmatch("([^|]*)|") do p[#p + 1] = x end
    if p[1] == "none" then g.Description = "InsideDev selection: " .. (p[2] or "")
    elseif p[1] == "object" then g.Description = "InsideDev selection: " .. (p[2] or "") .. "   (" .. (p[3] or "") .. ")"
    elseif p[1] == "comp" then
      comp = al.createMemoryRecord()
      comp.Description = (p[2] or "") .. "   script " .. (p[3] ~= "0" and p[3] or "-") .. "   native " .. (p[4] ~= "0" and p[4] or "-")
      comp.IsGroupHeader = true
      comp.appendToEntry(g)
    elseif p[1] == "rec" and p[4] then
      local r = al.createMemoryRecord()
      r.Description = p[2]
      r.Type = types[p[3]] or vtDword
      r.Address = p[4]
      if p[5] == "1" then r.ShowAsHex = true end
      r.appendToEntry(comp or g)
    end
  end
  g.Active = false
end

ask = function(q, silent)
  if not ensureDir() then
    if not silent then showMessage("InsideDev: attach Cheat Engine to INSIDE.exe first (and InsideDev must be installed in that game folder).") end
    return
  end
  D.askId = D.askId + 1
  os.remove(D.dir .. "\\answer.txt")
  local f = io.open(D.dir .. "\\ask.txt", "w")
  if not f then
    if not silent then showMessage("InsideDev: cannot write " .. D.dir .. "\\ask.txt") end
    return
  end
  f:write(D.askId .. "|" .. q); f:close()
  D.lastAsk = os.clock()
  D.waiting = { id = tostring(D.askId), q = q, t = os.clock(), silent = silent }
end

local function tick()
  local pid = getOpenedProcessID()
  if pid ~= D.pid then
    if D.dir then
      os.remove(D.dir .. "\\.link")
      clearGroup("not attached to INSIDE")
    end
    D.pid = pid; D.dir = nil; D.noGame = false; D.failed = nil; D.complained = false; D.stamp = nil; D.waiting = nil
  end
  if pid == 0 then return end
  if not ensureDir() then return end
  if os.clock() - D.beat >= HEARTBEAT and not writeLink() then
    D.dir = nil; D.failed = os.clock()
    return
  end
  local s = readAll(D.dir .. "\\selection.txt")
  if s then
    local lines = {}
    for l in s:gmatch("[^\r\n]+") do lines[#lines + 1] = l end
    local st = lines[1]
    if st and st ~= D.stamp then
      D.stamp = st
      -- "stamp|n" or "stamp|n|pid": a file of another game session holds addresses of a process that is gone
      local owner = st:match("^stamp|[^|]*|(%d+)")
      if owner == nil or tonumber(owner) == pid then
        table.remove(lines, 1)
        D.lastSelection = os.clock()
        local ok, e = pcall(rebuild, lines); if not ok then log(e) end
      end
    end
  end
  if D.waiting then
    local a = readAll(D.dir .. "\\answer.txt")
    local id, text
    if a then id, text = a:match("^([^\r\n]*)\r?\n(.*)$") end
    if id ~= nil and id == D.waiting.id then
      local w = D.waiting
      D.waiting = nil; D.lastAnswer = os.clock()
      os.remove(D.dir .. "\\answer.txt")
      if not w.silent then showMessage(text) end
    else
      if id ~= nil then os.remove(D.dir .. "\\answer.txt") end
      if os.clock() - D.waiting.t > ANSWER_TIMEOUT then
        local w = D.waiting
        D.waiting = nil
        if not w.silent then showMessage("InsideDev did not answer. Is the game running with InsideDev, and not paused, minimized or in a loading screen?") end
      end
    end
  end
end

local function hex(a) return string.format("%X", a) end

local function ago(t)
  if not t then return "never" end
  return string.format("%.0f s ago", os.clock() - t)
end

local function status()
  local pid = getOpenedProcessID()
  local lines = {}
  if pid == 0 then lines[#lines + 1] = "Cheat Engine is not attached to a process."
  else lines[#lines + 1] = "Cheat Engine is attached to process " .. pid .. (D.dir and " (INSIDE)" or (D.noGame and " (not INSIDE: the link is off)" or "")) end
  if D.dir then
    lines[#lines + 1] = "link folder: " .. D.dir
    lines[#lines + 1] = "heartbeat (.link) written " .. ago(D.beat)
    local sel = "selection received " .. ago(D.lastSelection)
    if lfs and lfs.attributes then
      local ok, m = pcall(lfs.attributes, D.dir .. "\\selection.txt", "modification")
      if ok and type(m) == "number" then sel = sel .. string.format("; selection.txt is %d s old", os.time() - m)
      else sel = sel .. "; no selection.txt (the game has not written one: is it running, and not paused?)" end
    end
    lines[#lines + 1] = sel
  elseif pid ~= 0 and not D.noGame then
    lines[#lines + 1] = "link folder: not found or not writable (see the Lua console); InsideDev must be installed in the game folder"
  end
  lines[#lines + 1] = "last question asked " .. ago(D.lastAsk) .. ", last answer " .. ago(D.lastAnswer) .. (D.waiting and " (waiting for an answer now)" or "")
  showMessage(table.concat(lines, "\n"))
end

local function addItem(menu, caption, fn)
  if menu == nil then return false end
  local mi = createMenuItem(menu)
  mi.Caption = caption
  mi.OnClick = function() local ok, e = pcall(fn); if not ok then showMessage("InsideDev: " .. tostring(e)) end end
  local okItems, items = pcall(function() return menu.Items end)
  if okItems and items ~= nil then items.add(mi) else menu.add(mi) end
  return true
end

-- the main menu "Table": by component name first (the caption is translated in localized Cheat Engine builds)
local function tableMenu()
  local ok, m = pcall(function() return MainForm.miTable end)
  if ok and m ~= nil then return m end
  ok, m = pcall(function() return MainForm.miSignTable.Parent end)
  if ok and m ~= nil then return m end
  local mm = MainForm.Menu
  if mm then
    for i = 0, mm.Items.Count - 1 do
      local it = mm.Items[i]
      if (it.Caption:gsub("&", "")) == "Table" then return it end
    end
  end
  return nil
end

local function install()
  if D.installed then return end
  D.installed = true
  -- address list
  addItem(MainForm.PopupMenu2, "InsideDev: what is this?", function()
    local r = getAddressList().getSelectedRecord()
    if r == nil then showMessage("select a record first"); return end
    ask("what " .. hex(r.CurrentAddress))
  end)
  -- memory viewer
  local mv = getMemoryViewForm()
  if mv then
    local dv = mv.DisassemblerView
    addItem(dv and dv.PopupMenu, "InsideDev: which method?", function() ask("method " .. hex(dv.SelectedAddress)) end)
    addItem(dv and dv.PopupMenu, "InsideDev: what is this?", function() ask("what " .. hex(dv.SelectedAddress)) end)
    local hv = mv.HexadecimalView
    addItem(hv and hv.PopupMenu, "InsideDev: what is this?", function() ask("what " .. hex(hv.SelectionStart)) end)
  end
  -- main menu "Table"; when it cannot be found the items go to the address list menu instead
  local ok, menu = pcall(tableMenu)
  if not ok or menu == nil then
    menu = MainForm.PopupMenu2
    log("the Table menu was not found in this Cheat Engine build: the InsideDev items are in the right-click menu of the address list")
  end
  addItem(menu, "InsideDev: what is at address...", function() local a = inputQuery("InsideDev", "Address (hex):", ""); if a and a ~= "" then ask("what " .. a) end end)
  addItem(menu, "InsideDev: which method at address...", function() local a = inputQuery("InsideDev", "Code address (hex):", ""); if a and a ~= "" then ask("method " .. a) end end)
  addItem(menu, "InsideDev: re-send selection", function() ask("refresh") end)
  addItem(menu, "InsideDev: link status", status)
end

if D.timer then D.timer.destroy() end
do
  local ok, e = pcall(install)
  if not ok then log("menu items not installed: " .. tostring(e)) end
end
if getOpenedProcessID() == 0 then pcall(openProcess, "INSIDE.exe") end
D.timer = createTimer(nil, false)
D.timer.Interval = 300
D.timer.OnTimer = function() local ok, e = pcall(tick); if not ok then log(e) end end
D.timer.Enabled = true
log("ready")
