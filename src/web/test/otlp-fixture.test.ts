// @vitest-environment node
/// <reference types="node" />
import { readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { SpanKind, ROOT_CONTEXT, trace } from '@opentelemetry/api'
import { JsonTraceSerializer } from '@opentelemetry/otlp-transformer'
import { resourceFromAttributes } from '@opentelemetry/resources'
import { BasicTracerProvider, InMemorySpanExporter, SimpleSpanProcessor } from '@opentelemetry/sdk-trace-base'
import { describe, expect, it } from 'vitest'

// The collector's tests read this hand-written fixture; it must keep the shape the real serializer produces.
const fixturePath = fileURLToPath(new URL('../../../tests/CodeAtlas.Collector.Tests/Fixtures/otlp/js-export.json', import.meta.url))

type Json = Record<string, unknown>
const shape = (value: unknown): string => Array.isArray(value) ? 'array' : value === null ? 'null' : typeof value

describe('OTLP/JSON fixture', () => {
  it('matches the shape of what @opentelemetry/exporter-trace-otlp-http sends', () => {
    const exporter = new InMemorySpanExporter()
    const provider = new BasicTracerProvider({
      resource: resourceFromAttributes({ 'service.name': 'sample-web' }),
      spanProcessors: [new SimpleSpanProcessor(exporter)],
    })
    const tracer = provider.getTracer('codeatlas-web')
    const interaction = tracer.startSpan('interaction: Create order', { attributes: { 'codeatlas.interaction.name': 'Create order', 'codeatlas.correlation': 'explicit' } }, ROOT_CONTEXT)
    const client = tracer.startSpan('POST /api/orders', {
      kind: SpanKind.CLIENT,
      attributes: { 'http.request.method': 'POST', 'url.path': '/api/orders', 'http.response.status_code': 201, 'codeatlas.correlation': 'explicit' },
    }, trace.setSpan(ROOT_CONTEXT, interaction))
    client.end()
    interaction.end()

    const real = JSON.parse(new TextDecoder().decode(JsonTraceSerializer.serializeRequest(exporter.getFinishedSpans())!)) as Json
    const fixture = JSON.parse(readFileSync(fixturePath, 'utf8')) as Json

    const spans = (doc: Json) => ((doc.resourceSpans as Json[])[0].scopeSpans as Json[])[0].spans as Json[]
    const realClient = spans(real).find(s => s.name === 'POST /api/orders')!
    const fixtureClient = spans(fixture).find(s => s.name === 'POST /api/orders')!
    for (const field of ['traceId', 'spanId', 'parentSpanId', 'kind', 'startTimeUnixNano', 'endTimeUnixNano'])
      expect(`${field}: ${shape(fixtureClient[field])}`).toBe(`${field}: ${shape(realClient[field])}`)
    expect(realClient.traceId).toMatch(/^[0-9a-f]{32}$/)
    expect(fixtureClient.traceId).toMatch(/^[0-9a-f]{32}$/)

    const status = (s: Json) => ((s.attributes as Json[]).find(a => a.key === 'http.response.status_code')!.value as Json).intValue
    expect(`intValue: ${shape(status(fixtureClient))}`).toBe(`intValue: ${shape(status(realClient))}`)
  })
})
