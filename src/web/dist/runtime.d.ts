import { type SpanExporter } from '@opentelemetry/sdk-trace-base';
import { type AxiosLikeConfig } from './registry';
export interface CodeAtlasOptions {
    /** OTLP/HTTP traces URL of the collector; may be relative to the page (e.g. a dev-server proxy path). */
    collectorUrl: string;
    serviceName: string;
    /** Requests started this long after a click/submit are linked to it heuristically. Default 1000 ms. */
    windowMs?: number;
    /** Cross-origin URLs (prefix or RegExp) that may receive a traceparent header. Same-origin always may. */
    propagateTo?: Array<string | RegExp>;
    /** Use button/link text as interaction labels. Default true. */
    textLabels?: boolean;
    /** Test hook: export synchronously to this exporter instead of OTLP. */
    exporter?: SpanExporter;
}
export interface CodeAtlasHandle {
    readonly collectorUrl: string;
    shutdown(): Promise<void>;
}
export declare function initCodeAtlas(options: CodeAtlasOptions): CodeAtlasHandle;
export type { AxiosLikeConfig };
