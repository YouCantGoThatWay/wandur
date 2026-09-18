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
    private Engine? _engine;
    private JsValue? _dispatch;
    public bool IsRunning { get; private set; }

    public ScriptResult Load(string source)
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
            _dispatch = _engine.Evaluate(Bootstrap);
            _engine.Execute(source);
            IsRunning = true;
            return Dispatch(new ScriptEvent("flush"));
        }
        catch (Exception error) { return Fail(error.Message); }
    }

    public ScriptResult Dispatch(ScriptEvent input)
    {
        if (!IsRunning || _engine is null || _dispatch is null) return new(false, []);
        if (input.Text.Length > MaximumEventCharacters) return Fail("Event text exceeds 32768 characters.");
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
        return new(false, [], message[..Math.Min(message.Length, 2048)]);
    }

    private const string Bootstrap = """
        (() => {
            const aliases = [], triggers = [], timers = [], listeners = [];
            let actions = [], outputSize = 0, clock = 0, hooks = 0;
            const stringify = JSON.stringify, parse = JSON.parse;
            const tag = Function.call.bind(Object.prototype.toString);
            function checkCallback(callback) {
                if (typeof callback !== 'function' || tag(callback) !== '[object Function]')
                    throw new TypeError('Callbacks must be synchronous functions.');
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
                if (kind === 'send' && (!text.length || text.length > 4096 || /[\x00-\x1f\x7f-\x9f\u2028\u2029]/.test(text)))
                    throw new RangeError('Commands must contain 1–4096 characters and no control characters.');
                if (text.length > 8192) throw new RangeError('Echo exceeds 8192 characters.');
                if (++outputSize + text.length > 32768 || actions.length >= 32)
                    throw new RangeError('Event action limit exceeded.');
                outputSize += text.length;
                actions.push({ Kind: kind, Text: text });
            }
            function call(callback, argument) {
                const result = callback(argument);
                if (result && typeof result.then === 'function')
                    throw new TypeError('Async callbacks are not supported.');
            }
            Object.defineProperty(globalThis, 'Events', { value: Object.freeze({ Line: 'line', Gmcp: 'gmcp', Key: 'key' }), writable: false, configurable: false });
            Object.defineProperty(globalThis, 'mud', { value: Object.freeze({
                send: text => action('send', text),
                echo: text => action('echo', text),
                alias: (pattern, callback) => register(aliases, pattern, callback),
                trigger: (pattern, callback) => register(triggers, pattern, callback),
                on: (event, callback) => {
                    if (event !== 'line' && event !== 'gmcp' && event !== 'key') throw new TypeError('Event must be line, gmcp or key.');
                    checkCallback(callback);
                    listeners.push({ event, callback });
                },
                every: (seconds, callback) => {
                    if (typeof seconds !== 'number' || !Number.isFinite(seconds) || seconds < 1 || seconds > 2147483)
                        throw new RangeError('Timer interval must be 1–2147483 seconds.');
                    checkCallback(callback);
                    timers.push({ interval: seconds * 1000, due: clock + seconds * 1000, callback });
                }
            }) });
            return (kind, text, elapsed) => {
                let handled = false;
                if (kind !== 'flush') { actions = []; outputSize = 0; }
                clock = Math.max(clock, elapsed);
                let event = null;
                if (kind === 'line' || kind === 'key') event = Object.freeze({ text });
                else if (kind === 'gmcp') {
                    const match = /^([A-Za-z][A-Za-z0-9_.]*)(?:\s+([\s\S]*))?$/.exec(text);
                    if (match) {
                        try { event = { package: match[1], data: match[2] ? parse(match[2]) : null }; }
                        catch (_) { /* Malformed server data is not a script failure. */ }
                    }
                }
                if (event !== null) {
                    for (const listener of listeners.slice()) {
                        if (listener.event === kind) call(listener.callback, event);
                    }
                }
                if (kind === 'command' || kind === 'line') {
                    const list = (kind === 'command' ? aliases : triggers).slice();
                    for (const hook of list) {
                        hook.expression.lastIndex = 0;
                        const match = hook.expression.exec(text);
                        if (match) {
                            call(hook.callback, match);
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
                } else if (kind !== 'flush' && kind !== 'gmcp' && kind !== 'key') throw new TypeError('Unknown script event.');
                const result = stringify({ Handled: handled, Actions: actions, Error: null });
                actions = []; outputSize = 0;
                return result;
            };
        })()
        """;
}
