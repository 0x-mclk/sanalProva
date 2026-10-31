import { useCreditBalance, type CreditQuote } from '../../shared/api/credits';
import { Notice } from '../../shared/ui';

/**
 * Ana ekran kredi uyarısı (Faz C): yalnızca GERÇEK (ücretli) modda ve kredi zorunluyken görünür; mock modda üretim kredi harcamaz.
 * Staff/Manager yalnızca kullanılabilir krediyi görür (defter ve tarife Owner ekranındadır). Yalnızca arayüz uyarısıdır: karar sunucudadır.
 */
export function LowCreditNotice() {
  const balance = useCreditBalance();
  const data = balance.data;
  if (!data || !data.enforced || !data.live) {
    return null;
  }
  if (data.isEmpty) {
    return <Notice tone="danger">Kredi bitti: yeni prova üretilemez. İşletme sahibine bildirin; kredi yüklendiğinde devam edilir.</Notice>;
  }
  if (data.isLow) {
    return <Notice tone="warning">Kalan kredi azaldı: {data.available} kredi. İşletme sahibine kredi yükleme gerektiğini bildirin.</Notice>;
  }
  return null;
}

/** Üretim öncesi bedel bilgisi (Faz C). Bu tutar kabulde sunucuda yeniden hesaplanır; burada yalnızca bilgi verilir. */
export function CreditQuoteNotice({ quote }: { quote: CreditQuote | undefined }) {
  if (!quote) {
    return null;
  }
  if (!quote.live) {
    return <Notice tone="info">Demo üretim: kredi harcanmaz ve sonuç gerçek yapay zekâ çıktısı değildir.</Notice>;
  }
  if (!quote.enforced || quote.cost === 0) {
    return null;
  }
  if (!quote.sufficient) {
    return (
      <Notice tone="danger">
        Bu üretim {quote.cost} kredi harcar, ancak kalan krediniz {quote.available}. İşletme sahibine kredi yükletin.
      </Notice>
    );
  }
  return (
    <Notice tone={quote.lowAfter ? 'warning' : 'info'}>
      Bu üretim <strong>{quote.cost} kredi</strong> harcar (kalan: {quote.available} → {quote.availableAfter}). Üretim başarısız olursa kredi iade edilir.
      {quote.lowAfter ? ' Kredi azalıyor: işletme sahibine haber verin.' : ''}
    </Notice>
  );
}
