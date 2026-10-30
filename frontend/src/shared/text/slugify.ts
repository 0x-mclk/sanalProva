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
