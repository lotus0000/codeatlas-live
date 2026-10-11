// The part of CodeAtlas that app code imports. Without the development runtime it is plain fetch / plain Axios,
// so it is safe (and tiny) in production bundles.
import { currentRuntime } from './registry';
/**
 * Links every request made through `ix` to an interaction named `name`, across any number of awaits.
 * Errors thrown by `fn` propagate unchanged.
 */
export async function trackInteraction(name, fn) {
    const runtime = currentRuntime();
    if (!runtime)
        return fn({ name, fetch: (input, init) => fetch(input, init), axiosConfig: () => ({}) });
    const handle = runtime.startInteraction(name);
    try {
        const result = await fn({ name, fetch: (input, init) => runtime.fetch(handle, input, init), axiosConfig: () => ({ codeatlas: handle }) });
        runtime.endInteraction(handle);
        return result;
    }
    catch (error) {
        runtime.endInteraction(handle, error);
        throw error;
    }
}
/** Adds trace context to requests made with `ix.axiosConfig()`. Other requests, and everything without the runtime, are untouched. */
export function instrumentAxios(instance) {
    instance.interceptors.request.use((config) => currentRuntime()?.beforeAxiosRequest(config) ?? config);
    instance.interceptors.response.use((response) => {
        currentRuntime()?.afterAxiosResponse(response.config, response.status);
        return response;
    }, (error) => {
        currentRuntime()?.afterAxiosResponse(error?.config, error?.response?.status ?? 0, error);
        return Promise.reject(error);
    });
}
