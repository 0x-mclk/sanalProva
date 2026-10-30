import { Button, Menu } from '@mantine/core';
import { Link, useNavigate } from 'react-router';
import { useLogout, useProducts, useSessions } from '../../shared/api/hooks';
import { useAuth } from '../../shared/auth/AuthContext';
import { tr, timeAgo } from '../../shared/i18n/tr';
import {
  ArrowRightIcon,
  ChevronRightIcon,
  Eyebrow,
  HangerIcon,
  HistoryIcon,
  LayersIcon,
  LogoutIcon,
  PlusIcon,
  ShieldIcon,
  SparklesIcon,
  StoreIcon,
  TvIcon,
  TvBadge,
} from '../../shared/ui';
import classes from './HomePage.module.css';

function greeting(hour: number) {
  if (hour < 12) return 'Günaydın';
  if (hour < 18) return 'İyi günler';
  return 'İyi akşamlar';
}

function initials(name: string) {
  return name
    .split(/\s+/)
    .filter(Boolean)
    .slice(0, 2)
    .map((part) => part[0]!.toLocaleUpperCase('tr-TR'))
    .join('');
}

/** Personel ana ekranı (referans: "Personel ana ekranı"). */
export function HomePage() {
  const { me, activeStore, setActiveStore, canManageCatalog, canManageStaff, isOwner } = useAuth();
  const navigate = useNavigate();
  const logout = useLogout();
  const sessions = useSessions('open', 5);
  const sets = useProducts({ type: 'OutfitSet', selectable: true, pageSize: 12 });

  const now = new Date();
  const firstName = me?.displayName.split(/\s+/)[0] ?? '';
  const dateText = new Intl.DateTimeFormat('tr-TR', { day: 'numeric', month: 'long', weekday: 'long' }).format(now).toLocaleUpperCase('tr-TR');
  const logoText = (me?.organizationName.split(/\s+/)[0] ?? 'lâl').toLocaleLowerCase('tr-TR');
  const heroImage = sets.data?.items.find((p) => p.cover)?.cover;
  const open = sessions.data?.items ?? [];

  return (
    <main className="sp-page">
      <header className={classes.header}>
        <div className={classes.brand}>
          <span className={classes.logo}>{logoText}</span>
          <span className={classes.brandText}>
            <Eyebrow tone="muted">Sanal Prova</Eyebrow>
            <span className={classes.store}>{activeStore?.name}</span>
          </span>
        </div>
        <TvBadge />
      </header>

      <div className={classes.greeting}>
        <div>
          <Eyebrow tone="muted">{dateText}</Eyebrow>
          <h1 className="sp-title">
            {greeting(now.getHours())}, {firstName}.
          </h1>
        </div>
        <Menu position="bottom-end" width={240} shadow="md" radius="md">
          <Menu.Target>
            <button type="button" className={classes.avatar} aria-label="Hesap menüsü">
              {initials(me?.displayName ?? '')}
            </button>
          </Menu.Target>
          <Menu.Dropdown>
            <Menu.Label>{me?.email}</Menu.Label>
            {me && me.stores.length > 1 ? (
              <>
                <Menu.Label>Şube</Menu.Label>
                {me.stores.map((store) => (
                  <Menu.Item key={store.id} leftSection={<StoreIcon size={16} />} onClick={() => setActiveStore(store.id)} fw={store.id === activeStore?.id ? 700 : 400}>
                    {store.name}
                  </Menu.Item>
                ))}
                <Menu.Divider />
              </>
            ) : null}
            <Menu.Item leftSection={<HistoryIcon size={16} />} component={Link} to="/gecmis">
              Prova geçmişi
            </Menu.Item>
            {canManageCatalog ? (
              <Menu.Item leftSection={<LayersIcon size={16} />} component={Link} to="/katalog">
                Katalog yönetimi
              </Menu.Item>
            ) : null}
            {canManageStaff ? (
              <Menu.Item leftSection={<StoreIcon size={16} />} component={Link} to="/yonetim/personel">
                Personel yönetimi
              </Menu.Item>
            ) : null}
            {isOwner ? (
              <Menu.Item leftSection={<StoreIcon size={16} />} component={Link} to="/yonetim/subeler">
                Şube yönetimi
              </Menu.Item>
            ) : null}
            {canManageCatalog ? (
              <Menu.Item leftSection={<HistoryIcon size={16} />} component={Link} to="/maliyet">
                AI kullanım ve maliyet
              </Menu.Item>
            ) : null}
            <Menu.Divider />
            <Menu.Item leftSection={<LogoutIcon size={16} />} color="red" onClick={() => logout.mutate(undefined, { onSettled: () => void navigate('/giris') })}>
              Çıkış yap
            </Menu.Item>
          </Menu.Dropdown>
        </Menu>
      </div>

      <section className={classes.hero} aria-labelledby="hero-title">
        {heroImage ? <img className={classes.heroImage} src={heroImage.url} alt="" /> : null}
        <div className={classes.heroShade} />
        <div className={classes.heroBody}>
          <Eyebrow tone="inverse" icon={<SparklesIcon />}>
            AI Sanal Prova
          </Eyebrow>
          <h2 id="hero-title" className={classes.heroTitle}>
            Hayalindeki kıyafet, birkaç adımda.
          </h2>
          <p className={classes.heroText}>Fotoğraf çekin, kıyafeti seçin. Sonucu büyük ekranda birlikte inceleyin.</p>
          <Button component={Link} to="/prova/yeni" leftSection={<PlusIcon size={18} />} rightSection={<ArrowRightIcon size={18} />} className={classes.heroButton}>
            Yeni Prova Başlat
          </Button>
        </div>
      </section>

      <div className={classes.columns}>
        <section aria-labelledby="open-title">
          <div className={classes.sectionHead}>
            <h2 id="open-title" className={classes.sectionTitle}>
              Devam Eden Provalar
            </h2>
            {open.length > 0 ? <span className={classes.count}>{sessions.data?.total ?? open.length} prova</span> : null}
          </div>
          {open.length === 0 ? (
            <p className={classes.empty}>{sessions.isLoading ? 'Yükleniyor…' : 'Devam eden prova yok. Yeni bir prova başlatabilirsiniz.'}</p>
          ) : (
            <ul className={classes.list}>
              {open.map((session) => (
                <li key={session.id}>
                  <Link to={`/prova/${session.id}`} className={classes.row}>
                    <span className={classes.rowIcon}>
                      <HangerIcon size={20} />
                    </span>
                    <span className={classes.rowText}>
                      <span className={classes.rowTitle}>
                        {session.customerLabel ?? 'Müşteri'} · {session.code}
                      </span>
                      <span className={classes.rowSub}>{tr.sessionStep[session.nextStep] ?? session.nextStep}</span>
                    </span>
                    <span className={classes.rowTime}>{timeAgo(session.lastActivityAt)}</span>
                    <ChevronRightIcon size={18} className={classes.chevron} />
                  </Link>
                </li>
              ))}
            </ul>
          )}
        </section>

        <div className={classes.tiles}>
          <Link to={canManageCatalog ? '/katalog' : '/katalog'} className={classes.tile}>
            <LayersIcon size={22} className={classes.tileIcon} />
            <span className={classes.tileTitle}>Ürün Kataloğu</span>
            <span className={classes.tileSub}>{sets.data ? `${sets.data.total} hazır takım` : '—'}</span>
          </Link>
          <Link to="/tv-cihazlar" className={classes.tile}>
            <TvIcon size={22} className={classes.tileIcon} />
            <span className={classes.tileTitle}>TV Bağlantısı</span>
            <span className={classes.tileSub}>{tr.app.tvNotConnected}</span>
          </Link>
        </div>
      </div>

      <p className={classes.footer}>
        <ShieldIcon size={16} /> Müşteri fotoğrafları güvenle, yalnızca prova için.
      </p>
    </main>
  );
}
