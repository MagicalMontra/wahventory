local Sync = require("Macro Sync.macro")

if not Sync.Call("Clear Inventory(wah)", 450) then return end

-- Continue your next task here, after actual refresh and discard completion.
yield("/e Inventory cleanup finished; continuing.")
