/**
 * Kamera üzerine çizilen boydan çekim kılavuzu (referans: kesikli çerçeve içinde ince çizgili figür).
 * Yalnızca görsel yardımdır; kadraj denetimi yükleme sırasında yön, çözünürlük ve netlik üzerinden yapılır.
 */
export function BodyGuide({ className }: { className?: string }) {
  return (
    <svg className={className} viewBox="0 0 200 300" preserveAspectRatio="xMidYMid meet" fill="none" aria-hidden="true" focusable="false">
      <rect x="22" y="14" width="156" height="272" rx="34" stroke="#D9BE7C" strokeOpacity="0.55" strokeWidth="1.5" strokeDasharray="5 6" />
      <g stroke="#E7D3A0" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round">
        <circle cx="100" cy="64" r="15" />
        <path d="M92 80v10M108 80v10" />
        <path d="M70 100c10-8 20-10 30-10s20 2 30 10l8 54-12 3-6-34v36l6 70h-18l-8-60-8 60H72l6-70v-36l-6 34-12-3 8-54z" />
      </g>
    </svg>
  );
}
