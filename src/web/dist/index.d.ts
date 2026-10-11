import { type InteractionHandle } from './registry';
export type { InteractionHandle } from './registry';
export interface Interaction {
    readonly name: string;
    /** fetch that carries this interaction's trace context. Without the runtime it is plain fetch. */
    fetch(input: RequestInfo | URL, init?: RequestInit): Promise<Response>;
    /** Pass as (or spread into) the config of a request made with an instance given to instrumentAxios. */
    axiosConfig(): {
        codeatlas?: InteractionHandle;
    };
}
/**
 * Links every request made through `ix` to an interaction named `name`, across any number of awaits.
 * Errors thrown by `fn` propagate unchanged.
 */
export declare function trackInteraction<T>(name: string, fn: (ix: Interaction) => Promise<T>): Promise<T>;
type Callback = (value: any) => any;
type Interceptors = {
    request: {
        use(onFulfilled: Callback): unknown;
    };
    response: {
        use(onFulfilled: Callback, onRejected?: Callback): unknown;
    };
};
/** The part of an Axios instance CodeAtlas needs; a real `AxiosInstance` fits. */
export interface AxiosLikeInstance {
    interceptors: Interceptors;
}
/** Adds trace context to requests made with `ix.axiosConfig()`. Other requests, and everything without the runtime, are untouched. */
export declare function instrumentAxios(instance: AxiosLikeInstance): void;
