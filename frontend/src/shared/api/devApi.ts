import { apiFetch } from './http';

// Sözleşme: backend/src/SanalProva.Api/Features/Dev/DevEndpoints.cs ve Features/Health/HealthChecks.cs.
// Aşama 2'den itibaren tipler OpenAPI dokümanından otomatik üretilecek.

export type GenerationJobType = 'OutfitSet' | 'FreeCombination' | 'Recolor' | 'ProductSwap' | 'VariantPreview';

export type HealthReport = {
  status: 'Healthy' | 'Degraded' | 'Unhealthy';
  durationMs: number;
  checks: { name: string; status: string; description: string | null; durationMs: number }[];
};

export type DevStatus = {
  environment: string;
  version: string;
  runtime: string;
  serverTime: string;
  database: {
    canConnect: boolean;
    server: string | null;
    database: string | null;
    appliedMigrations: string[];
    pendingMigrations: string[];
  };
  useMockProviders: boolean;
  strategies: { jobType: GenerationJobType; provider: string; configuredProvider: string; model: string; fallback: GenerationJobType | null }[];
};

export type MockGenerationResult = {
  jobType: GenerationJobType;
  provider: string;
  model: string;
  providerRequestId: string | null;
  durationMs: number;
  outputBytes: number;
};

/** /health/ready 503 de dönebilir; gövde yine rapordur. */
export async function getReadiness(): Promise<HealthReport> {
  const response = await fetch('/health/ready', { headers: { Accept: 'application/json' } });
  if (response.status !== 200 && response.status !== 503) {
    throw new Error(`HTTP ${response.status}`);
  }
  return (await response.json()) as HealthReport;
}

export const getDevStatus = () => apiFetch<DevStatus>('/api/v1/dev/status');

export const runMockGeneration = (jobType: GenerationJobType) =>
  apiFetch<MockGenerationResult>('/api/v1/dev/mock-generation', { method: 'POST', json: { jobType } });
