import { Badge, Button, Card, Group, SimpleGrid, Stack, Text, Title } from '@mantine/core';
import { useMutation, useQuery } from '@tanstack/react-query';
import { getDevStatus, getReadiness, runMockGeneration, type GenerationJobType } from '../../shared/api/devApi';
import { ApiError } from '../../shared/api/http';
import { errorMessage } from '../../shared/i18n/tr';
import { AppBar, Eyebrow, TvStatusBadge } from '../../shared/ui';
import { DesignPreview } from './DesignPreview';
import { useDiagnosticsHub } from './useDiagnosticsHub';
import classes from './DevStatusPage.module.css';

const statusColor: Record<string, string> = { Healthy: 'yesil', Degraded: 'altin', Unhealthy: 'red' };
const statusLabel: Record<string, string> = { Healthy: 'Sağlıklı', Degraded: 'Kısıtlı', Unhealthy: 'Hatalı' };

function StatusBadge({ status }: { status: string }) {
  return <Badge color={statusColor[status] ?? 'krem'}>{statusLabel[status] ?? status}</Badge>;
}

function Row({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <div className={classes.row}>
      <Text size="sm" c="dimmed">
        {label}
      </Text>
      <div className={classes.value}>{children}</div>
    </div>
  );
}

/**
 * Aşama 1 geliştirme ekranı: frontend → backend → PostgreSQL → AI strateji/mock → SignalR zincirini doğrular
 * ve tasarım token'larını gösterir. Yalnızca geliştirme içindir; gerçek uygulama ekranları Aşama 2'de.
 */
export function DevStatusPage() {
  const readiness = useQuery({ queryKey: ['health', 'ready'], queryFn: getReadiness, refetchInterval: 10_000 });
  const status = useQuery({ queryKey: ['dev', 'status'], queryFn: getDevStatus, retry: false });
  const hub = useDiagnosticsHub();
  const mock = useMutation({ mutationFn: (jobType: GenerationJobType) => runMockGeneration(jobType) });

  const apiError = (error: unknown) => (error instanceof ApiError ? errorMessage(error.code) : errorMessage('NETWORK_ERROR'));

  return (
    <main className="sp-page">
      <AppBar title="Geliştirme · Sistem durumu" trailing={<TvStatusBadge name="SignalR" state={hub.state} />} />

      <Stack gap="xl" mt="md">
        <div>
          <Eyebrow>Aşama 1 · Altyapı doğrulama</Eyebrow>
          <Title order={1} mt={6}>
            Sistem bağlantıları
          </Title>
          <Text c="dimmed" mt={6}>
            Bu ekran yalnızca geliştirme ortamı içindir. Gerçek AI çağrısı yapılmaz.
          </Text>
        </div>

        <SimpleGrid cols={{ base: 1, sm: 2 }} spacing="md">
          <Card>
            <Group justify="space-between" mb="sm">
              <Title order={4}>Backend</Title>
              {readiness.data ? <StatusBadge status={readiness.data.status} /> : <Badge color="red">Ulaşılamıyor</Badge>}
            </Group>
            {readiness.isError ? <Text size="sm">{apiError(readiness.error)}</Text> : null}
            {readiness.data?.checks.map((check) => (
              <Row key={check.name} label={check.name}>
                <StatusBadge status={check.status} />
                {check.description ? (
                  <Text size="xs" c="dimmed">
                    {check.description}
                  </Text>
                ) : null}
              </Row>
            ))}
          </Card>

          <Card>
            <Group justify="space-between" mb="sm">
              <Title order={4}>Veritabanı</Title>
              <Badge color={status.data?.database.canConnect ? 'yesil' : 'red'}>{status.data?.database.canConnect ? 'Bağlı' : 'Bağlı değil'}</Badge>
            </Group>
            {status.isError ? <Text size="sm">{apiError(status.error)}</Text> : null}
            {status.data ? (
              <>
                <Row label="Sunucu">{status.data.database.server}</Row>
                <Row label="Veritabanı">{status.data.database.database}</Row>
                <Row label="Uygulanan migration">{status.data.database.appliedMigrations.length}</Row>
                <Row label="Bekleyen migration">
                  <Badge color={status.data.database.pendingMigrations.length ? 'altin' : 'yesil'}>{status.data.database.pendingMigrations.length}</Badge>
                </Row>
                <Row label="Ortam">
                  {status.data.environment} · {status.data.runtime}
                </Row>
              </>
            ) : null}
          </Card>

          <Card>
            <Group justify="space-between" mb="sm">
              <Title order={4}>AI sağlayıcı stratejisi</Title>
              {status.data ? (
                <Badge color={status.data.useMockProviders ? 'yesil' : 'red'}>{status.data.useMockProviders ? 'Mock mod' : 'Gerçek API'}</Badge>
              ) : null}
            </Group>
            {status.data?.strategies.map((s) => (
              <Row key={s.jobType} label={s.jobType}>
                <Text size="sm">
                  {s.configuredProvider} · <span className={classes.mono}>{s.model}</span>
                </Text>
                <Group gap={6}>
                  <Badge variant="outline" color={s.provider === 'Mock' ? 'yesil' : 'krem'}>
                    → {s.provider}
                  </Badge>
                  {s.jobType !== 'Recolor' ? (
                    <Button size="compact-xs" variant="subtle" loading={mock.isPending && mock.variables === s.jobType} onClick={() => mock.mutate(s.jobType)}>
                      Dene
                    </Button>
                  ) : null}
                </Group>
              </Row>
            ))}
            {mock.data ? (
              <Text size="xs" c="dimmed" mt="sm">
                Son deneme: {mock.data.jobType} → {mock.data.provider} ({mock.data.model}), {mock.data.durationMs} ms, {mock.data.outputBytes} bayt
              </Text>
            ) : null}
            {mock.isError ? (
              <Text size="xs" c="red" mt="sm">
                {apiError(mock.error)}
              </Text>
            ) : null}
          </Card>

          <Card>
            <Group justify="space-between" mb="sm">
              <Title order={4}>Gerçek zamanlı (SignalR)</Title>
              <TvStatusBadge name="Hub" state={hub.state} />
            </Group>
            <Row label="Taşıma">{hub.transport ?? '—'}</Row>
            <Row label="Son ping">
              {hub.lastPing ? `${hub.lastPing.roundTripMs} ms · ${new Date(hub.lastPing.pong.serverTime).toLocaleTimeString('tr-TR')}` : '—'}
            </Row>
            {hub.error ? (
              <Text size="xs" c="red">
                {hub.error}
              </Text>
            ) : null}
            <Button size="md" variant="default" mt="md" fullWidth disabled={hub.state !== 'connected'} onClick={() => void hub.ping()}>
              Ping gönder
            </Button>
          </Card>
        </SimpleGrid>

        <DesignPreview />
      </Stack>
    </main>
  );
}
