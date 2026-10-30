import { Button, Stack, TextInput } from '@mantine/core';
import { useQueryClient } from '@tanstack/react-query';
import { useRef, useState, type FormEvent } from 'react';
import { Link, useNavigate } from 'react-router';
import { ApiError } from '../../shared/api/http';
import { createOrganization, platformKeys } from '../../shared/api/platform';
import type { CreateOrganizationResponse } from '../../shared/api/types';
import { errorMessage } from '../../shared/i18n/tr';
import { Notice } from '../../shared/ui';
import { newIdempotencyKey, suggestSlug, validateOrganizationForm, type OrganizationForm } from './createOrganization';
import { SetupLinkReveal } from './SetupLinkReveal';

const empty: OrganizationForm = { name: '', slug: '', storeName: '', storeCode: '', ownerDisplayName: '', ownerEmail: '' };

/**
 * `/platform/yeni`: firma + ilk şube + Owner. Kurulum bağlantısı YALNIZCA ilk yanıtta gelir ve yalnızca bu bileşenin yerel durumunda tutulur (önbelleğe/depolamaya yazılmaz).
 * Idempotency-Key form açılışında üretilir; sonucu belirsiz hatada (ağ/5xx) AYNI anahtarla tekrar gönderilir (sunucu aynı kimlikleri döner, bağlantıyı yeniden göstermez).
 */
export function CreateOrganizationPage() {
  const navigate = useNavigate();
  const client = useQueryClient();
  const [form, setForm] = useState<OrganizationForm>(empty);
  const [slugEdited, setSlugEdited] = useState(false);
  const [fieldErrors, setFieldErrors] = useState<Partial<Record<keyof OrganizationForm, string>>>({});
  const [error, setError] = useState<string | null>(null);
  const [sending, setSending] = useState(false);
  const [result, setResult] = useState<CreateOrganizationResponse | null>(null);
  const key = useRef(newIdempotencyKey());
  const inFlight = useRef(false);

  const update = (patch: Partial<OrganizationForm>) => setForm((current) => ({ ...current, ...patch }));

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    if (inFlight.current) return; // çift tıklama: ikinci istek gönderilmez
    const errors = validateOrganizationForm(form);
    setFieldErrors(errors);
    if (Object.keys(errors).length > 0) return;

    inFlight.current = true;
    setSending(true);
    setError(null);
    try {
      const created = await createOrganization(
        {
          name: form.name.trim(),
          slug: form.slug.trim(),
          storeName: form.storeName.trim(),
          storeCode: form.storeCode.trim(),
          ownerEmail: form.ownerEmail.trim(),
          ownerDisplayName: form.ownerDisplayName.trim(),
        },
        key.current,
      );
      setResult(created);
      void client.invalidateQueries({ queryKey: platformKeys.organizations });
    } catch (caught) {
      const code = caught instanceof ApiError ? caught.code : 'UNKNOWN';
      setError(errorMessage(code));
      // Kesin reddedilen (4xx) istek hiçbir şey oluşturmadı: yeni deneme yeni anahtar alır. Belirsiz sonuçta (ağ/5xx) aynı anahtar korunur.
      if (caught instanceof ApiError && caught.status >= 400 && caught.status < 500) {
        key.current = newIdempotencyKey();
      }
    } finally {
      inFlight.current = false;
      setSending(false);
    }
  };

  if (result) {
    const reveal = result.setupLinkShown && result.setupLink;
    return (
      <Stack gap="md" mt="md">
        <h1 className="sp-title">İşletme oluşturuldu</h1>
        {reveal ? (
          <SetupLinkReveal
            link={result.setupLink!}
            expiresAt={result.setupLinkExpiresAt}
            onClose={() => {
              const id = result.organizationId;
              setResult(null); // bağlantı bellekten silinir
              void navigate(`/platform/${id}`);
            }}
          />
        ) : (
          <>
            <Notice tone="warning">İşletme daha önce oluşturulmuş; kurulum bağlantısı yalnızca ilk yanıtta gösterilir. Gerekirse işletme sayfasından yeniden üretin.</Notice>
            <Button component={Link} to={`/platform/${result.organizationId}`}>
              İşletmeye git
            </Button>
          </>
        )}
      </Stack>
    );
  }

  return (
    <form onSubmit={(event) => void submit(event)} noValidate>
      <Stack gap="md" mt="md" pb="xl">
        <Link to="/platform">← İşletmeler</Link>
        <h1 className="sp-title" style={{ margin: 0 }}>
          Yeni işletme
        </h1>
        <p className="sp-lead">Firmayı, ilk şubesini ve firma sahibini (Owner) oluşturur. Owner şifresini tek kullanımlık bağlantıyla kendisi belirler.</p>

        <TextInput
          label="Firma adı"
          value={form.name}
          error={fieldErrors.name}
          onChange={(e) => update({ name: e.currentTarget.value, ...(slugEdited ? {} : { slug: suggestSlug(e.currentTarget.value) }) })}
          required
        />
        <TextInput
          label="Kısa ad (adres)"
          description="Küçük harf, rakam ve tire. Sonradan değiştirilemez."
          value={form.slug}
          error={fieldErrors.slug}
          onChange={(e) => {
            setSlugEdited(true);
            update({ slug: e.currentTarget.value });
          }}
          required
        />
        <TextInput label="İlk şube adı" value={form.storeName} error={fieldErrors.storeName} onChange={(e) => update({ storeName: e.currentTarget.value })} required />
        <TextInput label="Şube kodu" description="Örn. MRK" value={form.storeCode} error={fieldErrors.storeCode} onChange={(e) => update({ storeCode: e.currentTarget.value })} required />
        <TextInput label="Firma sahibi (Owner) adı soyadı" value={form.ownerDisplayName} error={fieldErrors.ownerDisplayName} onChange={(e) => update({ ownerDisplayName: e.currentTarget.value })} required />
        <TextInput label="Firma sahibi e-posta" type="email" inputMode="email" value={form.ownerEmail} error={fieldErrors.ownerEmail} onChange={(e) => update({ ownerEmail: e.currentTarget.value })} required />

        {error ? <Notice tone="danger">{error}</Notice> : null}
        <Button type="submit" loading={sending} disabled={sending}>
          İşletmeyi oluştur
        </Button>
      </Stack>
    </form>
  );
}
