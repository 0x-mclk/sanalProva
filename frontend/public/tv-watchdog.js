// Uygulama 10 sn içinde açılmazsa (çalışma zamanı hatası, eski motor) anlaşılır mesaj göster
setTimeout(function () {
  if (!window.__tvBooted) {
    document.getElementById('tv-fallback').style.display = 'block';
  }
}, 10000);
