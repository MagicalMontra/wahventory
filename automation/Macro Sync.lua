-- Save this as an SND Lua macro named: Macro Sync
-- Import from other macros: local Sync = require("Macro Sync.macro")
-- Do not run this helper by itself.
-- One worker execution / launcher at a time per name; this is not a lock.

local Sync = {}
local TEMP = assert(os.getenv("TEMP"), "[Macro Sync] TEMP is unavailable")
local STATES = { RUNNING = true, DONE = true, FAILED = true }

local function hex(text)
    return (text:gsub(".", function(c)
        return string.format("%02x", string.byte(c))
    end))
end

local function unhex(text)
    return (text:gsub("%x%x", function(pair)
        return string.char(tonumber(pair, 16))
    end))
end

local function pathFor(name)
    assert(type(name) == "string" and #name > 0 and #name <= 64,
        "[Macro Sync] Name must contain 1-64 bytes")
    return TEMP .. "\\snd_macro_sync_v1_" .. hex(name) .. ".txt"
end

local function save(name, state)
    local data = table.concat({
        "SND_SYNC_V1", tostring(state.runId), state.status,
        tostring(state.startedAt), tostring(state.updatedAt),
        tostring(state.completedCount), hex(state.message or ""),
        "END_SND_SYNC", ""
    }, "\n")

    local file, err = io.open(pathFor(name), "wb")
    assert(file, "[Macro Sync] Open for writing failed: " .. tostring(err))
    local written, writeErr = file:write(data)
    local closed, closeErr = file:close()
    assert(written and closed,
        "[Macro Sync] Write failed: " .. tostring(writeErr or closeErr))
end

-- Returns state, or nil when no record exists; nil,error on a read problem.
function Sync.GetState(name)
    local file, err, code = io.open(pathFor(name), "rb")
    if not file then
        if code == 2 then return nil end -- File does not exist.
        return nil, "State read failed: " .. tostring(err)
    end

    local data, readErr = file:read("*a")
    file:close()
    if not data then return nil, "State read failed: " .. tostring(readErr) end

    local id, status, started, updated, completed, message = data:match(
        "^SND_SYNC_V1\n(%d+)\n([A-Z]+)\n(%d+)\n(%d+)\n(%d+)\n([0-9a-f]*)\nEND_SND_SYNC\n$"
    )
    if not id or not STATES[status] or #message % 2 ~= 0 then
        return nil, "State record is incomplete or invalid"
    end

    id, completed = tonumber(id), tonumber(completed)
    if not id or not completed or id < 1 or completed > id then
        return nil, "State counters are invalid"
    end
    return {
        runId = id, status = status,
        startedAt = tonumber(started), updatedAt = tonumber(updated),
        completedCount = completed, message = unhex(message)
    }
end

local function readChecked(name)
    local state, err = Sync.GetState(name)
    assert(not err, "[Macro Sync] " .. tostring(err))
    return state
end

-- Call once at the START of an execution. IDs increase within this name.
function Sync.Begin(name)
    local previous = readChecked(name)
    local now = os.time()
    local state = {
        runId = (previous and previous.runId or 0) + 1,
        status = "RUNNING", startedAt = now, updatedAt = now,
        completedCount = previous and previous.completedCount or 0,
        message = ""
    }
    save(name, state)
    return state.runId
end

-- Every update must identify its execution. DONE/FAILED are terminal.
function Sync.SetState(name, status, runId, message)
    assert(STATES[status], "[Macro Sync] Unknown status: " .. tostring(status))
    local state = assert(readChecked(name), "[Macro Sync] No execution recorded")
    assert(state.runId == runId, "[Macro Sync] A different execution owns the state")
    assert(state.status == "RUNNING" or state.status == status,
        "[Macro Sync] Cannot change a completed/failed execution")

    if status == "DONE" and state.status ~= "DONE" then
        state.completedCount = state.completedCount + 1
    end
    state.status, state.updatedAt = status, os.time()
    state.message = tostring(message or "")
    save(name, state)
    return state
end

function Sync.HasRun(name)
    return readChecked(name) ~= nil
end

function Sync.HasCompleted(name)
    local state = readChecked(name)
    return state ~= nil and state.completedCount > 0
end

-- Returns true,state or false,reason. Never accepts a different run's signal.
-- os.time is wall-clock based with one-second resolution.
function Sync.WaitFor(name, status, timeout, runId, poll)
    timeout, poll = timeout or 30, poll or 0.25
    assert(STATES[status], "[Macro Sync] Unknown target status")
    assert(type(runId) == "number" and runId >= 1 and runId % 1 == 0,
        "[Macro Sync] A positive integer runId is required")
    assert(type(timeout) == "number" and timeout >= 0 and timeout < math.huge,
        "[Macro Sync] Timeout must be a finite nonnegative number")
    assert(type(poll) == "number" and poll >= 0.01 and poll < math.huge,
        "[Macro Sync] Poll interval must be at least 0.01 seconds")

    local deadline = os.time() + timeout
    local lastReadError
    while true do
        local state, err = Sync.GetState(name)
        lastReadError = err
        if state then
            if state.runId > runId then
                return false, "Run " .. runId .. " was superseded by run " .. state.runId
            elseif state.runId == runId then
                if state.status == status then return true, state end
                if state.status == "FAILED" then
                    return false, "Worker failed: " .. state.message
                elseif state.status == "DONE" then
                    return false, "Worker finished before target state was observed"
                end
            end
        end

        if os.time() >= deadline then
            local detail = lastReadError and ("; " .. lastReadError) or ""
            return false, "Timed out waiting for " .. name .. " run " .. runId
                .. " to report " .. status .. detail
        end
        yield(string.format("/wait %.3f", poll))
    end
end

-- Launch one fresh execution, ignoring all signals from earlier runs.
-- Do not concurrently launch this same name from another controller/manual run.
function Sync.StartAndWait(name, timeout, poll)
    pathFor(name) -- Validate before issuing a command.
    assert(not name:find('["\r\n]'), "[Macro Sync] Invalid macro name for /snd run")
    local previous, err = Sync.GetState(name)
    if err then return false, err end
    local expectedRunId = (previous and previous.runId or 0) + 1
    yield('/snd run "' .. name .. '"')
    return Sync.WaitFor(name, "DONE", timeout, expectedRunId, poll)
end

-- Wrap actual work. Normal return => DONE; error or false,reason => FAILED.
-- This does not catch an SND forced stop, game exit, or failed game commands
-- that do not raise a Lua error. Explicitly verify asynchronous work yourself.
function Sync.Run(name, work)
    assert(type(work) == "function", "[Macro Sync] Work must be a function")
    local started, runId = pcall(Sync.Begin, name)
    if not started then return false, tostring(runId) end

    local ok, result, reason = pcall(work, runId)
    local success = ok and result ~= false
    local message = success and "" or tostring(ok and (reason or "Worker returned false") or result)
    local saved, err = pcall(Sync.SetState, name,
        success and "DONE" or "FAILED", runId, message)
    if not saved then return false, "Could not publish result: " .. tostring(err) end
    if not success then return false, message end
    return true, runId
end

-- Shared response handling. A false result does not exit the caller;
-- callers use: if not Sync.Call("Worker name", 120) then return end
local function reportResult(name, ok, result)
    if ok then return true, result end
    local message = ("[Macro Sync] " .. tostring(name) .. ": " .. tostring(result))
        :gsub("[%c]", " "):sub(1, 300)
    -- A reporting failure must not replace the original worker/wait failure.
    pcall(function() yield("/e " .. message) end)
    return false, result
end

-- Launch a fresh execution, wait for DONE, and report failure/timeout.
-- The timeout is a maximum, not a fixed delay.
function Sync.Call(name, timeout, poll)
    local called, ok, result = pcall(Sync.StartAndWait, name, timeout, poll)
    if not called then return reportResult(name, false, ok) end
    return reportResult(name, ok, result)
end

-- Publish RUNNING/DONE/FAILED around work and report any failure.
-- Put all worker actions inside the work function.
function Sync.Work(name, work)
    local called, ok, result = pcall(Sync.Run, name, work)
    if not called then return reportResult(name, false, ok) end
    return reportResult(name, ok, result)
end

return Sync
