import { execFileSync } from 'node:child_process';

/**
 * İzole `sanalprova_e2e` veritabanına sqlcmd ile bağlanma yardımcıları. Windows yerel örneği Windows kimliğiyle (-E),
 * CI/Docker SQL Server'ı bağlantı dizesindeki SQL girişiyle (parola SQLCMDPASSWORD ortam değişkeniyle; komut satırına yazılmaz) bağlanır.
 * Veritabanı adı `sanalprova_e2e` değilse hiçbir şey çalıştırılmaz.
 */
export function sqlcmdFor(connection: string): { args: string[]; env: NodeJS.ProcessEnv } {
  const server = /Server=([^;]+)/i.exec(connection)?.[1];
  const database = /Database=([^;]+)/i.exec(connection)?.[1];
  if (database !== 'sanalprova_e2e' || !server) throw new Error('E2E veritabanı doğrulanamadı.');
  const user = /User ID=([^;]+)/i.exec(connection)?.[1];
  const password = /Password=([^;]+)/i.exec(connection)?.[1];
  if (user && password) {
    return { args: ['-S', server, '-d', database, '-U', user], env: { ...process.env, SQLCMDPASSWORD: password } };
  }
  return { args: ['-S', server, '-d', database, '-E'], env: process.env };
}

export function runSqlcmd(extra: string[]): string {
  const { args, env } = sqlcmdFor(process.env.SANALPROVA_E2E_DB ?? '');
  return execFileSync('sqlcmd', [...args, '-C', '-I', '-f', '65001', '-b', ...extra], { encoding: 'utf8', env, stdio: ['ignore', 'pipe', 'pipe'] });
}
