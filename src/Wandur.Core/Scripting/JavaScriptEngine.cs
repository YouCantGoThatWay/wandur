using System.Text;
using System.Text.Json;
using Jint;
using Jint.Native;

namespace Wandur.Core.Scripting;

/// <summary>Synchronous per-run engine, used only inside an isolated script worker.</summary>
public sealed class JavaScriptEngine
{
    public const int MaximumSourceBytes = 256 * 1024;
    public const int MaximumEventCharacters = 32768;
    public const int MaximumPanelActionCharacters = 65536;
    public const int MaximumPanelCharacters = 262144;
    /// <summary>A state seed may carry two full buckets, so it is the one event allowed past the 32 KiB limit.</summary>
    public const int MaximumStateCharacters = 1024 * 1024;
    /// <summary>Distinct MSDP variables one script may ask the world to report by reading them.</summary>
    public const int MaximumReportsPerScript = 64;
    /// <summary>32 send and echo actions, 32 panel actions and 64 report actions.</summary>
    public const int MaximumActions = 128;
    private Engine? _engine;
    private JsValue? _dispatch;
    private string? _pendingState;
    public bool IsRunning { get; private set; }

    /// <summary>An MSDP variable name the client will put in a REPORT request: letters, digits and
    /// underscore, 1 to 128 characters, not starting with a digit.</summary>
    public static bool IsValidMsdpName(string name)
        => name.Length is >= 1 and <= 128 && !char.IsAsciiDigit(name[0]) && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    /// <summary>Widget kinds a script may declare on a panel. The reference document is checked against this list.</summary>
    public static IReadOnlyList<string> PanelWidgetKinds { get; } =
        ["gauge", "label", "text", "list", "table", "button", "toggle", "input", "separator", "group"];

    public ScriptResult Load(string source, bool restrictedSend = false)
    {
        IsRunning = false;
        _engine = null;
        _dispatch = null;
        if (Encoding.UTF8.GetByteCount(source) > MaximumSourceBytes) return Fail("Source exceeds 256 KiB.");
        try
        {
            _engine = new Engine(options =>
            {
                options.LimitMemory(64 * 1024 * 1024).MaxStatements(100000)
                    .TimeoutInterval(TimeSpan.FromMilliseconds(300)).LimitRecursion(64);
                options.Constraints.RegexTimeout = TimeSpan.FromMilliseconds(100);
                options.Constraints.MaxArraySize = 1000000;
                options.Host.StringCompilationAllowed = false;
            });
            // Only native JavaScript values are exposed. In particular, AllowClr is never enabled.
            // The send policy is baked into the bootstrap so no later message can relax it.
            _dispatch = _engine.Evaluate(Bootstrap.Replace("__RESTRICTED_SEND__", restrictedSend ? "true" : "false", StringComparison.Ordinal));
            // A seed sent ahead of the load fills mud.state before the first line of the script runs.
            if (_pendingState is { } seed) { _pendingState = null; _engine.Invoke(_dispatch, "state", seed, 0L); }
            _engine.Execute(source);
            IsRunning = true;
            return Dispatch(new ScriptEvent("flush"));
        }
        catch (Exception error) { return Fail(error.Message); }
    }

    public ScriptResult Dispatch(ScriptEvent input)
    {
        if (input.Kind == "state" && input.Text.Length > MaximumStateCharacters) return Fail("State seed exceeds 1048576 characters.");
        if (!IsRunning || _engine is null || _dispatch is null)
        {
            // The host sends the state seed before the load request; it is applied inside Load.
            if (input.Kind == "state") _pendingState = input.Text;
            return new(false, []);
        }
        if (input.Kind != "state" && input.Text.Length > MaximumEventCharacters) return Fail("Event text exceeds 32768 characters.");
        try
        {
            var json = _engine.Invoke(_dispatch, input.Kind, input.Text, input.ElapsedMilliseconds).AsString();
            return JsonSerializer.Deserialize<ScriptResult>(json) ?? throw new InvalidOperationException("Invalid script result.");
        }
        catch (Exception error) { return Fail(error.Message); }
    }

    private ScriptResult Fail(string message)
    {
        IsRunning = false;
        _engine = null;
        _dispatch = null;
        _pendingState = null;
        return new(false, [], message[..Math.Min(message.Length, 2048)]);
    }

    private const string Bootstrap = """
        (() => {
            const aliases = [], triggers = [], timers = [], listeners = [];
            const panels = new Map(), handlers = new Map();
            const state = { gmcp: {}, msdp: {} };
            const sizes = { gmcp: { entries: new Map(), total: 0 }, msdp: { entries: new Map(), total: 0 } };
            const requested = new Set();
            const msdpName = /^[A-Za-z_][A-Za-z0-9_]{0,127}$/, packageName = /^[A-Za-z][A-Za-z0-9_.]*$/;
            let actions = [], outputSize = 0, emitted = 0, panelCount = 0, panelSize = 0, clock = 0, hooks = 0;
            const restrictedSend = __RESTRICTED_SEND__;
            let sendAllowed = !restrictedSend;
            const stringify = JSON.stringify, parse = JSON.parse;
            const tag = Function.call.bind(Object.prototype.toString);
            const has = Function.call.bind(Object.prototype.hasOwnProperty);
            const unsafeKey = key => key === '__proto__' || key === 'constructor' || key === 'prototype';
            function checkFunction(callback) {
                if (typeof callback !== 'function' || tag(callback) !== '[object Function]')
                    throw new TypeError('Callbacks must be synchronous functions.');
            }
            function checkCallback(callback) {
                checkFunction(callback);
                if (++hooks > 256) throw new RangeError('Maximum 256 hooks per script.');
            }
            function register(list, pattern, callback) {
                checkCallback(callback);
                if (typeof pattern !== 'string' && !(pattern instanceof RegExp))
                    throw new TypeError('Pattern must be a string or RegExp.');
                const expression = new RegExp(pattern);
                if (expression.source.length > 4096) throw new RangeError('Pattern exceeds 4096 characters.');
                list.push({ expression, callback });
            }
            function action(kind, text) {
                if (typeof text !== 'string') throw new TypeError('Action text must be a string.');
                if (kind === 'send') {
                    if (!sendAllowed) throw new Error('Pack send policy: this script may send commands only from an alias or a panel button.');
                    if (!text.length || text.length > 4096 || /[\x00-\x1f\x7f-\x9f\u2028\u2029]/.test(text))
                        throw new RangeError('Commands must contain 1–4096 characters and no control characters.');
                }
                if (text.length > 8192) throw new RangeError('Echo exceeds 8192 characters.');
                if (++outputSize + text.length > 32768 || ++emitted > 32)
                    throw new RangeError('Event action limit exceeded.');
                outputSize += text.length;
                actions.push({ Kind: kind, Text: text });
            }
            function panelAction(message) {
                const text = stringify(message);
                if (text.length > 65536) throw new RangeError('A panel action exceeds 65536 characters.');
                if (++panelCount > 32) throw new RangeError('Maximum 32 panel actions per event.');
                if (panelSize + text.length > 262144) throw new RangeError('Panel output limit exceeded.');
                panelSize += text.length;
                actions.push({ Kind: 'panel', Text: text });
            }
            function identifier(value, name) {
                if (typeof value !== 'string' || !/^[A-Za-z0-9][A-Za-z0-9_.\-]{0,63}$/.test(value))
                    throw new TypeError(name + ' must be 1 to 64 characters of letters, digits, dot, dash or underscore.');
                return value;
            }
            function plain(value, name) {
                if (typeof value === 'number' && Number.isFinite(value)) value = String(value);
                else if (typeof value === 'boolean') value = String(value);
                if (typeof value !== 'string') throw new TypeError(name + ' must be a string.');
                if (value.length > 4096) throw new RangeError(name + ' exceeds 4096 characters.');
                return value;
            }
            function finite(value, name) {
                if (typeof value !== 'number' || !Number.isFinite(value)) throw new TypeError(name + ' must be a finite number.');
                return value;
            }
            function entries(value, name, limit) {
                if (!Array.isArray(value)) throw new TypeError(name + ' must be an array.');
                if (value.length > limit) throw new RangeError(name + ' exceeds ' + limit + ' items.');
                return value.map(entry => plain(entry, name));
            }
            function hook(panel, widget, event, callback) {
                if (callback === undefined || callback === null) return;
                const key = panel + '\u0000' + widget + '\u0000' + event;
                if (handlers.has(key)) checkFunction(callback); else checkCallback(callback);
                handlers.set(key, callback);
            }
            function widget(record, id, kind, properties) {
                id = identifier(id, 'A widget id');
                if (properties === undefined || properties === null) properties = {};
                if (typeof properties !== 'object') throw new TypeError('Widget properties must be an object.');
                if (!record.widgets.has(id) && record.widgets.size >= 64) throw new RangeError('Maximum 64 widgets per panel.');
                const out = {};
                if (kind === 'gauge') {
                    if (properties.label !== undefined) out.label = plain(properties.label, 'label');
                    out.value = finite(properties.value === undefined ? 0 : properties.value, 'value');
                    out.max = finite(properties.max === undefined ? 100 : properties.max, 'max');
                    if (properties.warn !== undefined) out.warn = finite(properties.warn, 'warn');
                } else if (kind === 'label' || kind === 'text') {
                    out.text = plain(properties.text === undefined ? '' : properties.text, 'text');
                } else if (kind === 'list') {
                    if (properties.title !== undefined) out.title = plain(properties.title, 'title');
                    out.items = entries(properties.items === undefined ? [] : properties.items, 'items', 500);
                    hook(record.id, id, 'select', properties.onSelect);
                } else if (kind === 'table') {
                    if (properties.title !== undefined) out.title = plain(properties.title, 'title');
                    out.columns = entries(properties.columns === undefined ? [] : properties.columns, 'columns', 32);
                    const rows = properties.rows === undefined ? [] : properties.rows;
                    if (!Array.isArray(rows)) throw new TypeError('rows must be an array.');
                    if (rows.length > 500) throw new RangeError('rows exceeds 500 items.');
                    out.rows = rows.map(row => entries(row, 'a row', 32));
                } else if (kind === 'button') {
                    out.label = plain(properties.label === undefined ? id : properties.label, 'label');
                    hook(record.id, id, 'click', properties.onClick);
                } else if (kind === 'toggle') {
                    out.label = plain(properties.label === undefined ? id : properties.label, 'label');
                    out.value = properties.value === true;
                    hook(record.id, id, 'change', properties.onChange);
                } else if (kind === 'input') {
                    if (properties.placeholder !== undefined) out.placeholder = plain(properties.placeholder, 'placeholder');
                    if (properties.value !== undefined) out.value = plain(properties.value, 'value');
                    hook(record.id, id, 'submit', properties.onSubmit);
                } else if (kind === 'group') {
                    if (properties.title !== undefined) out.title = plain(properties.title, 'title');
                    out.children = entries(properties.children === undefined ? [] : properties.children, 'children', 64);
                }
                record.widgets.add(id);
                panelAction({ panel: record.id, action: 'widget', widget: id, kind, props: out });
                return record.api;
            }
            function panel(id, options) {
                id = identifier(id, 'A panel id');
                if (options === undefined || options === null) options = {};
                if (typeof options !== 'object') throw new TypeError('Panel options must be an object.');
                const existing = panels.get(id);
                const title = options.title === undefined ? (existing === undefined ? id : existing.title) : plain(options.title, 'title');
                const dock = options.dock === undefined ? (existing === undefined ? 'right' : existing.dock) : options.dock;
                if (dock !== 'left' && dock !== 'right') throw new TypeError('A panel docks to left or right.');
                if (existing !== undefined) {
                    if (existing.title !== title || existing.dock !== dock) {
                        existing.title = title; existing.dock = dock;
                        panelAction({ panel: id, action: 'create', title, dock });
                    }
                    return existing.api;
                }
                if (panels.size >= 8) throw new RangeError('Maximum 8 panels per script.');
                const record = { id, title, dock, widgets: new Set(), api: null };
                panels.set(id, record);
                panelAction({ panel: id, action: 'create', title, dock });
                const kinds = ['gauge', 'label', 'text', 'list', 'table', 'button', 'toggle', 'input', 'separator', 'group'];
                const api = {};
                for (const kind of kinds) api[kind] = (widgetId, properties) => widget(record, widgetId, kind, properties);
                api.remove = widgetId => {
                    widgetId = identifier(widgetId, 'A widget id');
                    record.widgets.delete(widgetId);
                    for (const event of ['click', 'change', 'submit', 'select'])
                        handlers.delete(id + '\u0000' + widgetId + '\u0000' + event);
                    panelAction({ panel: id, action: 'remove', widget: widgetId });
                    return record.api;
                };
                api.show = () => { panelAction({ panel: id, action: 'show' }); return record.api; };
                api.hide = () => { panelAction({ panel: id, action: 'hide' }); return record.api; };
                api.close = () => {
                    panels.delete(id);
                    for (const key of Array.from(handlers.keys())) if (key.indexOf(id + '\u0000') === 0) handlers.delete(key);
                    panelAction({ panel: id, action: 'close' });
                };
                record.api = Object.freeze(api);
                return record.api;
            }
            function store(bucket, key, value) {
                if (unsafeKey(key)) return undefined;
                let text;
                try { text = stringify(value === undefined ? null : value); } catch (_) { return undefined; }
                if (typeof text !== 'string' || text.length > 32768) return undefined;
                const bin = sizes[bucket];
                const previous = bin.entries.has(key) ? bin.entries.get(key) : 0;
                if (!bin.entries.has(key) && bin.entries.size >= 512) return undefined;
                if (bin.total - previous + text.length > 262144) return undefined;
                bin.total += text.length - previous;
                bin.entries.set(key, text.length);
                return parse(text);
            }
            function recordGmcp(name, data) {
                const parts = name.split('.');
                if (parts.length > 8 || parts.some(unsafeKey)) return;
                const copy = store('gmcp', name, data);
                if (copy === undefined) return;
                let node = state.gmcp;
                for (let i = 0; i < parts.length - 1; i++) {
                    const child = node[parts[i]];
                    if (child === null || typeof child !== 'object' || Array.isArray(child)) node[parts[i]] = {};
                    node = node[parts[i]];
                }
                node[parts[parts.length - 1]] = copy;
            }
            function recordMsdp(variable, value) {
                const copy = store('msdp', variable, value);
                if (copy === undefined) return;
                state.msdp[variable] = copy;
            }
            // The host's cache of everything received so far, applied through the live paths so the same
            // limits and copying hold. No listener runs: a seed is not new data from the world.
            function seed(text) {
                let parsed;
                try { parsed = parse(text); } catch (_) { return; }
                if (parsed === null || typeof parsed !== 'object') return;
                const gmcp = parsed.gmcp, msdp = parsed.msdp;
                if (gmcp !== null && typeof gmcp === 'object' && !Array.isArray(gmcp))
                    for (const name of Object.keys(gmcp)) if (packageName.test(name)) recordGmcp(name, gmcp[name]);
                if (msdp !== null && typeof msdp === 'object' && !Array.isArray(msdp))
                    for (const name of Object.keys(msdp)) recordMsdp(name, msdp[name]);
            }
            // A variable the world has not sent is asked for once per script; the answer arrives as Events.Msdp.
            function report(name) {
                if (!msdpName.test(name) || requested.has(name) || requested.size >= 64) return;
                requested.add(name);
                actions.push({ Kind: 'report', Text: name });
            }
            function call(callback, argument) {
                const result = callback(argument);
                if (result && typeof result.then === 'function')
                    throw new TypeError('Async callbacks are not supported.');
            }
            Object.defineProperty(globalThis, 'Events', { value: Object.freeze({ Line: 'line', Gmcp: 'gmcp', Key: 'key', Msdp: 'msdp' }), writable: false, configurable: false });
            Object.defineProperty(globalThis, 'mud', { value: Object.freeze({
                send: text => action('send', text),
                echo: text => action('echo', text),
                alias: (pattern, callback) => register(aliases, pattern, callback),
                trigger: (pattern, callback) => register(triggers, pattern, callback),
                on: (event, callback) => {
                    if (event !== 'line' && event !== 'gmcp' && event !== 'key' && event !== 'msdp')
                        throw new TypeError('Event must be line, gmcp, msdp or key.');
                    checkCallback(callback);
                    listeners.push({ event, callback });
                },
                every: (seconds, callback) => {
                    if (typeof seconds !== 'number' || !Number.isFinite(seconds) || seconds < 1 || seconds > 2147483)
                        throw new RangeError('Timer interval must be 1–2147483 seconds.');
                    checkCallback(callback);
                    timers.push({ interval: seconds * 1000, due: clock + seconds * 1000, callback });
                },
                panel: (id, options) => panel(id, options),
                state: Object.freeze({
                    get: path => {
                        if (typeof path !== 'string') throw new TypeError('A state path must be a string.');
                        if (path.length > 512) throw new RangeError('A state path exceeds 512 characters.');
                        const parts = path.split('.');
                        if (parts[0] === 'msdp' && parts.length > 1 && !has(state.msdp, parts[1])) report(parts[1]);
                        let node = state;
                        for (const part of parts) {
                            if (node === null || typeof node !== 'object' || !has(node, part)) return undefined;
                            node = node[part];
                        }
                        return node === null || typeof node !== 'object' ? node : parse(stringify(node));
                    },
                    snapshot: () => parse(stringify(state))
                })
            }) });
            return (kind, text, elapsed) => {
                let handled = false;
                if (kind !== 'flush') { actions = []; outputSize = 0; emitted = 0; panelCount = 0; panelSize = 0; }
                clock = Math.max(clock, elapsed);
                let event = null, message = null;
                if (kind === 'line' || kind === 'key') event = Object.freeze({ text });
                else if (kind === 'gmcp') {
                    const match = /^([A-Za-z][A-Za-z0-9_.]*)(?:\s+([\s\S]*))?$/.exec(text);
                    if (match) {
                        try {
                            const data = match[2] ? parse(match[2]) : null;
                            recordGmcp(match[1], data);
                            event = { package: match[1], data };
                        }
                        catch (_) { /* Malformed server data is not a script failure. */ }
                    }
                } else if (kind === 'msdp') {
                    try {
                        const parsed = parse(text);
                        if (parsed !== null && typeof parsed === 'object' && typeof parsed.variable === 'string') {
                            const value = parsed.value === undefined ? null : parsed.value;
                            recordMsdp(parsed.variable, value);
                            event = Object.freeze({ variable: parsed.variable, value });
                        }
                    }
                    catch (_) { /* Malformed server data is not a script failure. */ }
                } else if (kind === 'panel') {
                    try {
                        const parsed = parse(text);
                        if (parsed !== null && typeof parsed === 'object') message = parsed;
                    }
                    catch (_) { /* A malformed callback message is ignored. */ }
                } else if (kind === 'state') seed(text);
                sendAllowed = !restrictedSend || kind === 'command' || (message !== null && message.event === 'click');
                if (event !== null) {
                    for (const listener of listeners.slice()) {
                        if (listener.event === kind) call(listener.callback, event);
                    }
                }
                if (kind === 'command' || kind === 'line') {
                    const list = (kind === 'command' ? aliases : triggers).slice();
                    for (const entry of list) {
                        entry.expression.lastIndex = 0;
                        const match = entry.expression.exec(text);
                        if (match) {
                            call(entry.callback, match);
                            if (kind === 'command') { handled = true; break; }
                        }
                    }
                } else if (kind === 'tick') {
                    for (const timer of timers.slice()) {
                        if (clock >= timer.due) {
                            timer.due = clock + timer.interval;
                            call(timer.callback);
                        }
                    }
                } else if (kind === 'panel') {
                    if (message !== null) {
                        const handler = handlers.get(message.panel + '\u0000' + message.widget + '\u0000' + message.event);
                        if (handler !== undefined) call(handler, message.value === undefined ? null : message.value);
                    }
                } else if (kind !== 'flush' && kind !== 'gmcp' && kind !== 'key' && kind !== 'msdp' && kind !== 'state') throw new TypeError('Unknown script event.');
                const result = stringify({ Handled: handled, Actions: actions, Error: null });
                actions = []; outputSize = 0; emitted = 0; panelCount = 0; panelSize = 0;
                return result;
            };
        })()
        """;
}
