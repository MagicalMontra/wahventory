-- Save as an SND Lua macro named: Wahventory Sync
-- Import: local Wah = require("Wahventory Sync.macro")
-- Requires the Wahventory fork with completion status support (20.0.0.6+).
-- Run one cleanup/controller at a time. These files report results, not locks.

local Wah = {}
local operations = { refresh = "/wahventory refresh", discard = "/wahventory auto" }
local statuses = { IDLE = true, RUNNING = true, DONE = true, FAILED = true, CANCELLED = true }
local poll = 0.25
local runtime

local function initialize()
    if runtime then return runtime end
    local ok, result = pcall(function()
        local Environment = luanet.import_type("System.Environment")
        local Path = luanet.import_type("System.IO.Path")
        local pid = tonumber(Environment.ProcessId)
        local temp = tostring(Path.GetTempPath())
        local ticks = tonumber(Environment.TickCount64)
        assert(pid and pid > 0 and pid % 1 == 0, "Game process ID is unavailable")
        assert(temp and #temp > 0, "Temporary directory is unavailable")
        assert(ticks, "Monotonic clock is unavailable")
        if not temp:match("[/\\]$") then temp = temp .. "\\" end
        return { Environment = Environment, directory = temp, pid = pid }
    end)
    if not ok then return nil, "[Wahventory Sync] Initialization failed: " .. tostring(result) end
    runtime = result
    return runtime
end

local function pathFor(operation)
    if not operations[operation] then return nil, "Unsupported operation: " .. tostring(operation) end
    local rt, err = initialize()
    if not rt then return nil, err end
    return rt.directory .. "wahventory_status_v1_" .. tostring(rt.pid) .. "_" .. operation .. ".txt"
end

local function decodeHex(value)
    if #value % 2 ~= 0 or value:find("[^0-9A-F]") then return nil end
    return (value:gsub("..", function(pair) return string.char(tonumber(pair, 16)) end))
end

local function integer(value)
    local number = tonumber(value)
    if not number or number < 0 or math.type(number) ~= "integer" then return nil end
    return number
end

-- Returns true state data only after closing its file handle.
-- A missing/partially replaced file returns nil,reason, never a successful result.
function Wah.GetState(operation)
    local path, pathErr = pathFor(operation)
    if not path then return nil, pathErr end
    local opened, file, openErr = pcall(io.open, path, "rb")
    if not opened or not file then
        return nil, "Cannot read completion status for " .. operation .. ": " .. tostring(opened and openErr or file)
    end
    local readOk, data, readErr = pcall(file.read, file, "*a")
    local closeOk, closed, closeErr = pcall(file.close, file)
    if not readOk or type(data) ~= "string" then
        return nil, "Completion status read failed: " .. tostring(readOk and readErr or data)
    end
    if not closeOk or not closed then
        return nil, "Completion status close failed: " .. tostring(closeOk and closeErr or closed)
    end
    local session, op, id, status, completed, total, message = data:match(
        "^WAHVENTORY_STATUS_V1\n([%x%-]+)\n([a-z]+)\n(%d+)\n([A-Z]+)\n(%d+)\n(%d+)\n([0-9A-F]*)\nEND_WAHVENTORY_STATUS\n$"
    )
    if not session or not session:match("^%x%x%x%x%x%x%x%x%-%x%x%x%x%-%x%x%x%x%-%x%x%x%x%-%x%x%x%x%x%x%x%x%x%x%x%x$")
        or op ~= operation or not statuses[status] then
        return nil, "Completion status is incomplete or invalid for " .. operation
    end
    id, completed, total = integer(id), integer(completed), integer(total)
    message = decodeHex(message)
    if not id or not completed or not total or not message or completed > total
        or (status == "DONE" and completed ~= total)
        or (id == 0 and status ~= "IDLE") or (id > 0 and status == "IDLE") then
        return nil, "Completion status counters or message are invalid for " .. operation
    end
    return { session = session, operation = op, runId = id, status = status,
        completed = completed, total = total, message = message }
end

local function now()
    return tonumber(runtime.Environment.TickCount64) / 1000
end

local function execute(operation, timeout)
    if type(timeout) ~= "number" or timeout ~= timeout or timeout < 0 or timeout >= math.huge then
        return false, "[Wahventory Sync] Timeout must be a finite nonnegative number"
    end
    -- The fork publishes IDLE at startup. Do not issue an unobservable command
    -- against an older plugin, a different process, or an unavailable publisher.
    local baseline, baselineErr = Wah.GetState(operation)
    if not baseline then
        return false, "[Wahventory Sync] " .. tostring(baselineErr)
            .. ". Update/enable the Wahventory fork with completion support (20.0.0.6+) in this game, then retry. No command was sent."
    end
    local otherOperation = operation == "refresh" and "discard" or "refresh"
    local other, otherErr = Wah.GetState(otherOperation)
    if not other then
        return false, "[Wahventory Sync] " .. tostring(otherErr)
            .. ". Update/enable the Wahventory fork with completion support (20.0.0.6+), then retry. No command was sent."
    end
    if other.session ~= baseline.session then
        return false, "[Wahventory Sync] Wahventory is restarting; completion sessions differ. Retry after reload. No command was sent."
    end
    if baseline.status == "RUNNING" or other.status == "RUNNING" then
        local busy = baseline.status == "RUNNING" and baseline or other
        return false, "[Wahventory Sync] Wahventory " .. busy.operation .. " run " .. busy.runId
            .. " is already running. Wait for it to finish. No command was sent."
    end
    if baseline.runId == math.maxinteger then
        return false, "[Wahventory Sync] Operation counter is exhausted; reload Wahventory. No command was sent."
    end
    local expected = baseline.runId + 1
    local deadline = now() + timeout
    local commanded, commandErr = pcall(function() yield(operations[operation]) end)
    if not commanded then return false, "[Wahventory Sync] Command failed: " .. tostring(commandErr) end

    local lastReadError, lastState
    while true do
        local state, err = Wah.GetState(operation)
        lastReadError = err
        if state then
            lastState = state
            if state.session ~= baseline.session then
                return false, "[Wahventory Sync] Wahventory restarted while waiting for " .. operation
                    .. "; the original operation cannot be verified."
            elseif state.runId > expected then
                return false, "[Wahventory Sync] " .. operation .. " run " .. expected
                    .. " was superseded by run " .. state.runId .. "."
            elseif state.runId == expected then
                if state.status == "DONE" then return true, state end
                if state.status == "FAILED" or state.status == "CANCELLED" then
                    local detail = state.message ~= "" and (": " .. state.message) or ""
                    return false, "[Wahventory Sync] " .. operation .. " " .. state.status:lower() .. detail
                end
            end
            -- An old DONE is not this command's completion. A synchronous fresh
            -- DONE is accepted even if its RUNNING publication was never polled.
        end
        if now() >= deadline then
            local detail = lastReadError and ("; last read error: " .. lastReadError) or ""
            if lastState and lastState.runId == expected then
                detail = detail .. "; last state " .. lastState.status .. " "
                    .. lastState.completed .. "/" .. lastState.total
            end
            return false, "[Wahventory Sync] Timed out waiting for " .. operation .. " run " .. expected
                .. ". Check Wahventory before retrying; timeout does not cancel its work" .. detail
        end
        yield("/wait 0.25")
    end
end

local function run(operation, timeout)
    local called, ok, result = pcall(execute, operation, timeout)
    if not called then return false, "[Wahventory Sync] " .. tostring(ok) end
    return ok, result
end

-- true,state means this new command published DONE. false,reason means callers
-- must stop their dependent work; it does not imply the plugin was cancelled.
function Wah.Refresh(timeout) return run("refresh", timeout or 120) end
function Wah.Discard(timeout) return run("discard", timeout or 300) end

function Wah.Cleanup(refreshTimeout, discardTimeout)
    local ok, refresh = Wah.Refresh(refreshTimeout)
    if not ok then return false, refresh end
    local discarded, discard = Wah.Discard(discardTimeout)
    if not discarded then return false, discard end
    return true, { refresh = refresh, discard = discard }
end

return Wah
