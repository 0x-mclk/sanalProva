import { Button, Card, Chip, Group, SimpleGrid, Stack, Text, TextInput, Title } from '@mantine/core';
import { tr } from '../../shared/i18n/tr';
import { tokens } from '../../shared/theme/tokens';
import { ArrowRightIcon, CheckIcon, Eyebrow, SparklesIcon, StepProgress, TvStatusBadge } from '../../shared/ui';
import classes from './DevStatusPage.module.css';

const semanticSwatches = [
  ['Zemin', tokens.color.bg],
  ['Yüzey', tokens.color.surface],
  ['İkincil yüzey', tokens.color.surfaceMuted],
  ['Bordo (birincil)', tokens.color.primary],
  ['Altın (vurgu)', tokens.color.accent],
  ['Metin', tokens.color.text],
] as const;

/** Tasarım token'larının ve temel bileşenlerin canlı önizlemesi (referans: docs/design/reference.png). */
export function DesignPreview() {
  return (
    <Stack gap="md">
      <div>
        <Eyebrow icon={<SparklesIcon />}>Tasarım sistemi</Eyebrow>
        <Title order={2} mt={6}>
          Token önizlemesi
        </Title>
      </div>

      <Card>
        <Title order={4} mb="sm">
          Renkler
        </Title>
        <SimpleGrid cols={{ base: 3, sm: 6 }} spacing="sm">
          {semanticSwatches.map(([label, value]) => (
            <div key={label}>
              <div className={classes.swatch} style={{ background: value }} />
              <Text size="xs" mt={4}>
                {label}
              </Text>
              <Text size="xs" c="dimmed" className={classes.mono}>
                {value}
              </Text>
            </div>
          ))}
        </SimpleGrid>
        <Stack gap={6} mt="md">
          {Object.entries(tokens.palettes).map(([name, shades]) => (
            <div key={name}>
              <Text size="xs" c="dimmed">
                {name}
              </Text>
              <div className={classes.palette}>
                {shades.map((shade) => (
                  <span key={shade} style={{ background: shade }} title={shade} />
                ))}
              </div>
            </div>
          ))}
        </Stack>
      </Card>

      <Card>
        <Title order={4} mb="sm">
          Tipografi
        </Title>
        <Stack gap="xs">
          <Text c="dimmed" size="xs">
            20 EKİM SALI
          </Text>
          <Title order={1} style={{ fontSize: tokens.heading.display.size, lineHeight: tokens.heading.display.lineHeight }}>
            Hayalindeki kıyafet, birkaç adımda.
          </Title>
          <Title order={1}>Önce, boydan bir fotoğraf.</Title>
          <Title order={2}>Hazır Takım Seç</Title>
          <Title order={4}>Devam Eden Provalar</Title>
          <Text>Müşterinin başı ve ayakları kadrajda olsun. Kollarını gövdesinden hafifçe ayırmasını isteyin.</Text>
          <Text size="sm" c="dimmed">
            {tr.app.aiDisclaimer}
          </Text>
          <Group gap="md">
            <Eyebrow>Hızlı başlangıç</Eyebrow>
            <Eyebrow tone="muted">Çekim sonrası</Eyebrow>
          </Group>
        </Stack>
      </Card>

      <Card>
        <Title order={4} mb="sm">
          Bileşenler
        </Title>
        <Stack gap="md">
          <StepProgress steps={tr.steps.tryOn} current={1} />
          <Group gap="xs">
            <TvStatusBadge name="Salon TV" state="connected" />
            <TvStatusBadge name="Salon TV" state="connecting" />
            <TvStatusBadge name="Salon TV" state="offline" />
          </Group>
          <TextInput placeholder="Ürün adı veya kodu ara" aria-label="Ürün ara" />
          <Group gap="xs">
            <Chip checked>Tümü</Chip>
            <Chip checked={false}>Bindallı</Chip>
            <Chip checked={false}>Şalvar</Chip>
          </Group>
          <Button fullWidth leftSection={<SparklesIcon size={18} />}>
            Üret
          </Button>
          <SimpleGrid cols={2} spacing="sm">
            <Button size="md" variant="default">
              Yeniden Çek
            </Button>
            <Button size="md" leftSection={<ArrowRightIcon size={18} />}>
              Devam Et
            </Button>
          </SimpleGrid>
          <Button variant="subtle" size="md" rightSection={<ArrowRightIcon size={16} />}>
            Takımları keşfet
          </Button>
          <div className={classes.stickyBar}>
            <Group justify="space-between" mb="xs">
              <div>
                <Eyebrow tone="muted">1 takım seçildi</Eyebrow>
                <Text fw={600}>Sultan Bindallı · BD-014</Text>
              </div>
              <Text size="xs" c="dimmed">
                <CheckIcon size={12} /> Fotoğraf hazır
              </Text>
            </Group>
            <Button fullWidth leftSection={<SparklesIcon size={18} />}>
              Üret
            </Button>
          </div>
        </Stack>
      </Card>
    </Stack>
  );
}
