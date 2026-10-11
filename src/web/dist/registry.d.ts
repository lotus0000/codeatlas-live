export interface InteractionHandle {
    readonly name: string;
}
export type AxiosLikeConfig = {
    url?: string;
    baseURL?: string;
    method?: string;
    headers?: unknown;
    codeatlas?: InteractionHandle;
    [key: string]: unknown;
};
export interface Runtime {
    startInteraction(name: string): InteractionHandle;
    endInteraction(handle: InteractionHandle, error?: unknown): void;
    fetch(handle: InteractionHandle, input: RequestInfo | URL, init?: RequestInit): Promise<Response>;
    beforeAxiosRequest<C extends AxiosLikeConfig>(config: C): C;
    afterAxiosResponse(config: AxiosLikeConfig | undefined, status: number, error?: unknown): void;
}
export declare function currentRuntime(): Runtime | undefined;
export declare function setRuntime(runtime: Runtime | undefined): void;
