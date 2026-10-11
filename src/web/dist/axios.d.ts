import type { InteractionHandle } from './registry';
declare module 'axios' {
    interface AxiosRequestConfig {
        codeatlas?: InteractionHandle;
    }
}
export {};
