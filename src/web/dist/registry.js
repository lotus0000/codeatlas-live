// Shared between the pass-through API and the runtime. Nothing here imports OpenTelemetry.
const slot = Symbol.for('codeatlas.web.runtime');
export function currentRuntime() {
    return globalThis[slot];
}
export function setRuntime(runtime) {
    ;
    globalThis[slot] = runtime;
}
