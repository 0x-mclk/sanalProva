import { execFileSync } from 'node:child_process';
import { resolve } from 'node:path';

/**
 * API demo veriyi tohumladıktan sonra (webServer hazır) izolasyon senaryoları için ikinci şube ve ayrı bir firma ekler.
 * Yalnızca `sanalprova_e2e` veritabanında çalışır (config zaten bunu zorunlu kılar; burada tekrar doğrulanır).
 */
export default function globalSetup() {
  const connection = process.env.SANALPROVA_E2E_DB ?? '';
  const database = /Database=([^;]+)/i.exec(connection)?.[1];
  const server = /Server=([^;]+)/i.exec(connection)?.[1];
  if (database !== 'sanalprova_e2e' || !server) throw new Error('E2E veritabanı doğrulanamadı.');
  try {
    execFileSync('sqlcmd', ['-S', server, '-d', database, '-E', '-C', '-b', '-I', '-f', '65001', '-i', resolve(import.meta.dirname, 'isolation-seed.sql')], { stdio: 'pipe', encoding: 'utf8' });
  } catch (error) {
    throw new Error(`İzolasyon verisi eklenemedi: ${(error as { stdout?: string }).stdout ?? error}`, { cause: error });
  }
}
