import { resolve } from 'node:path';
import { runSqlcmd } from './sql';

/**
 * API demo veriyi tohumladıktan sonra (webServer hazır) izolasyon senaryoları için ikinci şube ve ayrı bir firma ekler.
 * Yalnızca `sanalprova_e2e` veritabanında çalışır (config zaten bunu zorunlu kılar; burada tekrar doğrulanır).
 */
export default function globalSetup() {
  try {
    runSqlcmd(['-i', resolve(import.meta.dirname, 'isolation-seed.sql')]);
  } catch (error) {
    throw new Error(`İzolasyon verisi eklenemedi: ${(error as { stdout?: string }).stdout ?? error}`, { cause: error });
  }
}
