import { Button, Modal, MultiSelect, NumberInput, Select, Stack, Switch, Textarea, TextInput } from '@mantine/core';
import { useState } from 'react';
import { useNavigate } from 'react-router';
import { useCategories, useSaveCategory } from '../../shared/api/hooks';
import { ApiError } from '../../shared/api/http';
import type { CategoryDto } from '../../shared/api/types';
import { errorMessage } from '../../shared/i18n/tr';
import { AppBar, EmptyState, ErrorState, LoadingState, Notice, PencilIcon, PlusIcon } from '../../shared/ui';
import classes from './CatalogAdmin.module.css';

const ZONES = [
  { value: 'Upper', label: 'Üst beden' },
  { value: 'Lower', label: 'Alt beden' },
  { value: 'FullBody', label: 'Tüm vücut' },
  { value: 'Waist', label: 'Bel' },
  { value: 'Accessory', label: 'Aksesuar' },
];

type Draft = Omit<CategoryDto, 'id' | 'aiHint'> & { id?: string; aiHint: string };

const blank = (order: number): Draft => ({ name: '', slug: '', sortOrder: order, bodyZone: 'Upper', layerOrder: 10, maxSelection: 1, conflictsWith: [], aiHint: '', isActive: true });

/** Türkçe karakterleri sadeleştirip kısa ad (slug) üretir: "Şalvar Takımı" → "salvar-takimi". */
export function slugify(text: string): string {
  return text
    .toLocaleLowerCase('tr-TR')
    .replace(/ı/g, 'i')
    .normalize('NFD')
    .replace(/\p{Diacritic}/gu, '')
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-+|-+$/g, '');
}

/** Kategori yönetimi: sıra, giyiliş katmanı, seçim kuralı (kategori başına en fazla kaç ürün) ve çakışan kategoriler. */
export function CategoriesPage() {
  const navigate = useNavigate();
  const categories = useCategories(true);
  const save = useSaveCategory();
  const [draft, setDraft] = useState<Draft | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [slugTouched, setSlugTouched] = useState(false);

  const submit = () => {
    if (!draft) return;
    setError(null);
    save.mutate(
      {
        id: draft.id,
        body: {
          name: draft.name.trim(), slug: draft.slug, sortOrder: draft.sortOrder, bodyZone: draft.bodyZone, layerOrder: draft.layerOrder,
          maxSelection: draft.maxSelection, conflictsWith: draft.conflictsWith, aiHint: draft.aiHint.trim() || null, isActive: draft.isActive,
        },
      },
      { onSuccess: () => setDraft(null), onError: (e) => setError(errorMessage(e instanceof ApiError ? e.code : 'UNKNOWN')) },
    );
  };

  const open = (category?: CategoryDto) => {
    setError(null);
    setSlugTouched(Boolean(category));
    setDraft(category ? { ...category, aiHint: category.aiHint ?? '', conflictsWith: [...category.conflictsWith] } : blank((categories.data?.length ?? 0) * 10 + 10));
  };

  return (
    <main className="sp-page">
      <AppBar title="Kategoriler" onBack={() => void navigate('/katalog')} />
      <h1 className="sp-title">Kategoriler</h1>
      <p className="sp-lead">Serbest kombinde görünen kategoriler, giyiliş sırası ve seçim kuralları buradan yönetilir. Yeni kategori kod değişikliği gerektirmez.</p>
      <div style={{ marginBottom: 'var(--sp-space-md)' }}>
        <Button size="md" leftSection={<PlusIcon size={18} />} onClick={() => open()}>
          Yeni kategori
        </Button>
      </div>

      {categories.isLoading ? <LoadingState /> : null}
      {categories.isError ? <ErrorState error={categories.error} onRetry={() => void categories.refetch()} /> : null}
      {categories.data?.length === 0 ? <EmptyState title="Kategori yok" description="İlk kategoriyi ekleyin." /> : null}

      <section className={classes.section} style={{ padding: 'var(--sp-space-md)' }}>
        {categories.data?.map((category, index) => (
          <div key={category.id} className={classes.variantRow} style={index === 0 ? { borderTop: 0 } : undefined}>
            <span className={classes.grow}>
              <strong>{category.name}</strong>
              <span style={{ display: 'block', fontSize: 'var(--sp-font-size-xs)', color: 'var(--sp-color-text-muted)' }}>
                {ZONES.find((z) => z.value === category.bodyZone)?.label} · katman {category.layerOrder} · en fazla {category.maxSelection} seçim
                {category.conflictsWith.length > 0 ? ` · ${category.conflictsWith.length} çakışma` : ''}
                {!category.isActive ? ' · pasif' : ''}
              </span>
            </span>
            <Button variant="subtle" color="dark" size="compact-md" leftSection={<PencilIcon size={16} />} onClick={() => open(category)}>
              Düzenle
            </Button>
          </div>
        ))}
      </section>

      <Modal opened={draft !== null} onClose={() => setDraft(null)} title={draft?.id ? 'Kategoriyi düzenle' : 'Yeni kategori'} centered radius="lg" padding="lg" size="lg">
        {draft ? (
          <Stack gap="sm">
            <TextInput
              label="Ad"
              required
              value={draft.name}
              maxLength={80}
              onChange={(e) => {
                const name = e.currentTarget.value;
                setDraft({ ...draft, name, slug: slugTouched ? draft.slug : slugify(name) });
              }}
              data-autofocus
            />
            <TextInput
              label="Kısa ad (adres)"
              description="Küçük harf, rakam ve tire. Örn. salvar-takimi"
              required
              value={draft.slug}
              maxLength={60}
              onChange={(e) => {
                setSlugTouched(true);
                setDraft({ ...draft, slug: e.currentTarget.value });
              }}
            />
            <div className={classes.row}>
              <Select label="Giyim bölgesi" value={draft.bodyZone} onChange={(v) => setDraft({ ...draft, bodyZone: (v ?? 'Upper') as Draft['bodyZone'] })} allowDeselect={false} data={ZONES} />
              <NumberInput label="Ekran sırası" value={draft.sortOrder} onChange={(v) => setDraft({ ...draft, sortOrder: Number(v) || 0 })} min={0} />
            </div>
            <div className={classes.row}>
              <NumberInput label="Giyiliş katmanı" description="İçten dışa: gömlek 10, yelek 20, kaftan 30, kemer 40" value={draft.layerOrder} onChange={(v) => setDraft({ ...draft, layerOrder: Number(v) || 0 })} min={0} max={1000} />
              <NumberInput label="En fazla seçim" description="Bu kategoriden bir kombine kaç ürün girebilir" value={draft.maxSelection} onChange={(v) => setDraft({ ...draft, maxSelection: Math.max(1, Number(v) || 1) })} min={1} max={10} />
            </div>
            <MultiSelect
              label="Birlikte seçilemeyen kategoriler"
              value={draft.conflictsWith}
              onChange={(v) => setDraft({ ...draft, conflictsWith: v })}
              data={(categories.data ?? []).filter((c) => c.id !== draft.id).map((c) => ({ value: c.id, label: c.name }))}
              clearable
            />
            <Textarea label="Yapay zekâ ipucu (yalnızca yöneticiler görür)" description='Giyim tarifi. Örn. "kemer belde, en üst katmanın üzerinde durur".' value={draft.aiHint} onChange={(e) => setDraft({ ...draft, aiHint: e.currentTarget.value })} maxLength={500} autosize minRows={2} />
            <Switch label="Aktif" checked={draft.isActive} onChange={(e) => setDraft({ ...draft, isActive: e.currentTarget.checked })} />
            {error ? <Notice tone="danger">{error}</Notice> : null}
            <Button size="md" disabled={!draft.name.trim() || !/^[a-z0-9]+(-[a-z0-9]+)*$/.test(draft.slug)} loading={save.isPending} onClick={submit}>
              Kaydet
            </Button>
          </Stack>
        ) : null}
      </Modal>
    </main>
  );
}
