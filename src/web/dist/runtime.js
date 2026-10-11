// Development only. Load it with `if (import.meta.env.DEV) void import('@codeatlas/web/runtime').then(...)`
// so production builds never contain it.
import { ROOT_CONTEXT, SpanKind, SpanStatusCode, trace } from '@opentelemetry/api';
import { W3CTraceContextPropagator } from '@opentelemetry/core';
import { OTLPTraceExporter } from '@opentelemetry/exporter-trace-otlp-http';
import { resourceFromAttributes } from '@opentelemetry/resources';
import { BatchSpanProcessor, SimpleSpanProcessor } from '@opentelemetry/sdk-trace-base';
import { WebTracerProvider } from '@opentelemetry/sdk-trace-web';
import { labelOf } from './labels';
import { setRuntime } from './registry';
const axiosSpan = Symbol('codeatlas.axiosSpan');
let active;
export function initCodeAtlas(options) {
    if (active)
        return active;
    const collectorUrl = new URL(options.collectorUrl, location.href).href;
    const propagateTo = options.propagateTo ?? [];
    const processor = options.exporter
        ? new SimpleSpanProcessor(options.exporter)
        : new BatchSpanProcessor(new OTLPTraceExporter({ url: collectorUrl }), { scheduledDelayMillis: 300 });
    const provider = new WebTracerProvider({
        resource: resourceFromAttributes({ 'service.name': options.serviceName }),
        spanProcessors: [processor],
    });
    const tracer = provider.getTracer('codeatlas-web');
    const propagator = new W3CTraceContextPropagator();
    // Whatever fetch the page has now (possibly the app's own wrapper) is what CodeAtlas calls.
    const original = window.fetch;
    const resolve = (input) => new URL(input instanceof Request ? input.url : String(input), location.href);
    const traceable = (url) => url.href !== collectorUrl &&
        (url.origin === location.origin || propagateTo.some(p => (typeof p === 'string' ? url.href.startsWith(p) : p.test(url.href))));
    const windowMs = options.windowMs ?? 1000;
    const textLabels = options.textLabels ?? true;
    let pending = null;
    function onInteraction(event, kind) {
        const target = event.target instanceof Element ? event.target : null;
        const element = kind === 'submit' ? target : target?.closest('[data-codeatlas],button,a,[role=button],input[type=submit],input[type=button]');
        if (!element)
            return;
        const submitter = kind === 'submit' ? event.submitter : null;
        const { label, source } = submitter ? labelOf(submitter, 'click', textLabels) : labelOf(element, kind, textLabels);
        pending = { label, source, event: kind, at: performance.now(), wallMs: Date.now() };
    }
    function heuristicSpan(p) {
        if (!p.span) {
            p.span = tracer.startSpan(`interaction: ${p.label}`, {
                startTime: p.wallMs,
                attributes: {
                    'codeatlas.interaction.name': p.label,
                    'codeatlas.interaction.label_source': p.source,
                    'codeatlas.interaction.event': p.event,
                    'codeatlas.correlation': 'heuristic',
                },
            }, ROOT_CONTEXT);
            p.span.end(p.wallMs);
        }
        return p.span;
    }
    function heuristicFetch(input, init) {
        const recent = pending && performance.now() - pending.at <= windowMs ? pending : null;
        const request = input instanceof Request ? input : undefined;
        const carriesTrace = new Headers(init?.headers ?? request?.headers).has('traceparent');
        // Checked here too, so an untraceable request (collector, cross-origin) never creates the interaction span.
        if (!recent || carriesTrace || !traceable(resolve(input)))
            return original(input, init);
        return tracedFetch(heuristicSpan(recent), 'heuristic', input, init);
    }
    const onClick = (event) => onInteraction(event, 'click');
    const onSubmit = (event) => onInteraction(event, 'submit');
    function startClient(parent, correlation, method, url) {
        return tracer.startSpan(`${method} ${url.pathname}`, {
            kind: SpanKind.CLIENT,
            // Path only: query strings and fragments may hold sensitive values.
            attributes: { 'http.request.method': method, 'url.path': url.pathname, 'codeatlas.correlation': correlation },
        }, trace.setSpan(ROOT_CONTEXT, parent));
    }
    function endClient(span, status, error) {
        if (!span)
            return;
        if (status)
            span.setAttribute('http.response.status_code', status);
        if (error || status >= 500)
            span.setStatus({ code: SpanStatusCode.ERROR, message: error ? String(error) : `HTTP ${status}` });
        span.end();
    }
    async function tracedFetch(parent, correlation, input, init) {
        const url = resolve(input);
        if (!traceable(url))
            return original(input, init);
        const request = input instanceof Request ? input : undefined;
        const method = (init?.method ?? request?.method ?? 'GET').toUpperCase();
        const span = startClient(parent, correlation, method, url);
        const headers = new Headers(init?.headers ?? request?.headers);
        propagator.inject(trace.setSpan(ROOT_CONTEXT, span), headers, { set: (carrier, key, value) => carrier.set(key, value) });
        try {
            const response = await original(input, { ...init, headers });
            endClient(span, response.status);
            return response;
        }
        catch (error) {
            endClient(span, 0, error);
            throw error;
        }
    }
    const runtime = {
        startInteraction(name) {
            pending = null;
            const span = tracer.startSpan(`interaction: ${name}`, {
                attributes: { 'codeatlas.interaction.name': name, 'codeatlas.correlation': 'explicit' },
            }, ROOT_CONTEXT);
            return { name, span };
        },
        endInteraction(handle, error) {
            const span = handle.span;
            if (error !== undefined)
                span.setStatus({ code: SpanStatusCode.ERROR, message: String(error) });
            span.end();
        },
        fetch: (handle, input, init) => tracedFetch(handle.span, 'explicit', input, init),
        beforeAxiosRequest(config) {
            const handle = config.codeatlas;
            if (!handle)
                return config;
            const base = config.baseURL ? new URL(config.baseURL, location.href) : new URL(location.href);
            const url = new URL(config.url ?? '', base);
            if (!traceable(url))
                return config;
            const span = startClient(handle.span, 'explicit', (config.method ?? 'get').toUpperCase(), url);
            const carrier = {};
            propagator.inject(trace.setSpan(ROOT_CONTEXT, span), carrier, { set: (c, key, value) => { c[key] = value; } });
            const headers = config.headers;
            if (headers && typeof headers.set === 'function')
                for (const [key, value] of Object.entries(carrier))
                    headers.set(key, value);
            else
                config.headers = { ...headers, ...carrier };
            config[axiosSpan] = span;
            return config;
        },
        afterAxiosResponse(config, status, error) {
            // Axios rejects every non-2xx; like fetch, only network errors and 5xx count as span errors.
            endClient(config?.[axiosSpan], status, status ? undefined : error);
        },
    };
    // The patch keeps OpenTelemetry's `__original` convention so the OTLP exporter bypasses it.
    const patched = Object.assign(((input, init) => heuristicFetch(input, init)), { __original: original });
    document.addEventListener('click', onClick, { capture: true, passive: true });
    document.addEventListener('submit', onSubmit, { capture: true, passive: true });
    window.fetch = patched;
    setRuntime(runtime);
    const handle = {
        collectorUrl,
        async shutdown() {
            document.removeEventListener('click', onClick, { capture: true });
            document.removeEventListener('submit', onSubmit, { capture: true });
            if (window.fetch === patched)
                window.fetch = original;
            setRuntime(undefined);
            active = undefined;
            await provider.shutdown();
        },
    };
    active = handle;
    return handle;
}
