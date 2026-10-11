import { computed, ref, watch } from 'vue'
import { LOCALES, LOCALE_INFO, strings, type Locale } from '../i18n/strings'

const KEY = 'opslookup.netcore.locale'
const RTL_LOCALES = new Set<string>(LOCALES.filter((l) => LOCALE_INFO[l].dir === 'rtl'))

/** Arabic by default — this app is being localised for Arabic-speaking staff. */
const locale = ref<Locale>('ar')

export function isRtl(l: Locale): boolean {
  return RTL_LOCALES.has(l)
}

function readStored(): Locale {
  const value = localStorage.getItem(KEY)
  return LOCALES.includes(value as Locale) ? (value as Locale) : 'ar'
}

/**
 * Sets `lang` and `dir` on <html>. `dir` is what flips the whole layout, and
 * Tailwind's logical utilities (`ps-`, `me-`, `text-start`) key off it, so the
 * components themselves carry no direction-specific branches.
 */
function applyDocument(l: Locale) {
  document.documentElement.lang = l
  document.documentElement.dir = LOCALE_INFO[l].dir
}

export function useI18n() {
  // Seeded eagerly rather than in onMounted: the first render already needs the
  // right language, and waiting for mount would flash English at an Arabic user.
  locale.value = readStored()
  applyDocument(locale.value)

  watch(locale, (next) => {
    localStorage.setItem(KEY, next)
    applyDocument(next)
  })

  const t = computed(() => strings[locale.value])
  const rtl = computed(() => isRtl(locale.value))

  /**
   * Western digits in both languages. Arabic-speaking business users in Egypt
   * and the Gulf read Latin digits, and a social ID is data — it must never be
   * rendered as Arabic-Indic digits or the number becomes wrong.
   */
  const number = (value: number | string) => String(value)

  return { locale, t, rtl, number, locales: LOCALES, localeInfo: LOCALE_INFO }
}