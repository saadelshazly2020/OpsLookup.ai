import { onMounted, ref, watch } from 'vue'

const KEY = 'opslookup.netcore.theme'
export type ThemeChoice = 'system' | 'light' | 'dark'

const choice = ref<ThemeChoice>('system')
const systemDark = ref(false)

function readStored(): ThemeChoice {
  const value = localStorage.getItem(KEY)
  return value === 'light' || value === 'dark' ? value : 'system'
}

/**
 * Puts `.dark` on <html>. Tailwind's dark variant keys off that class, and the
 * theme tokens in main.css re-declare themselves under it, so one class flips
 * the whole page.
 */
function apply() {
  const dark = choice.value === 'dark' || (choice.value === 'system' && systemDark.value)
  document.documentElement.classList.toggle('dark', dark)
}

export function useTheme() {
  onMounted(() => {
    choice.value = readStored()
    systemDark.value = window.matchMedia('(prefers-color-scheme: dark)').matches

    // Only meaningful while the choice is 'system', but the listener is cheap
    // and this avoids re-subscribing on every toggle.
    window
      .matchMedia('(prefers-color-scheme: dark)')
      .addEventListener('change', (event) => {
        systemDark.value = event.matches
        apply()
      })

    apply()
  })

  watch(choice, (next) => {
    if (next === 'system') localStorage.removeItem(KEY)
    else localStorage.setItem(KEY, next)
    apply()
  })

  return { choice }
}