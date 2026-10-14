-- Save as an SND Lua macro named: Clear Inventory(wah)
local NAME = "Clear Inventory(wah)"
local Sync = require("Macro Sync.macro")

Sync.Work(NAME, function()
    local Wah = require("Wahventory Sync.macro")
    yield("/e [Utilits: Clear Inventory] Refreshing")
    local refreshed, refresh = Wah.Refresh(120)
    if not refreshed then return false, refresh end

    yield("/e [Utilits: Clear Inventory] Discarding")
    local discarded, discard = Wah.Discard(300)
    if not discarded then return false, discard end
end)
