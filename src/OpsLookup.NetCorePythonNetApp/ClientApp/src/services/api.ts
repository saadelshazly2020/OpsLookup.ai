import type { Locale } from '../i18n/strings'
import type { ChatReply, Health } from '../types'

/**
 * Error text for transport-level failures, keyed by locale so a member is not
 * shown an English message on an Arabic page. Deliberately small: anything the
 * .NET host returns in `detail` is passed through as-is and cannot be
 * translated here.
 */
const TRANSPORT_ERRORS: Record<Locale, string> = {
  ar: 'تعذّر الوصول إلى الوكيل. تأكد من تشغيل تطبيق .NET.',
  en: 'Could not reach the agent. Check that the .NET app is running.',
}

/**
 * POST /api/chat.
 *
 * A 502 carries the reason the model call failed; a 503 means the embedded
 * Python graph is still starting. Both are surfaced as readable text rather
 * than a raw status code.
 *
 * `locale` is passed so the agent answers in the same language as the page.
 * Without it the model picks its own language, and the user reads an Arabic UI
 * next to an English answer.
 */
export async function sendChat(
  ticketId: string,
  message: string,
  locale: Locale = 'ar',
  signal?: AbortSignal,
): Promise<ChatReply> {
  let response: Response
  try {
    response = await fetch('/api/chat', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ ticketId, message, locale }),
      signal,
    })
  } catch {
    throw new Error(TRANSPORT_ERRORS[locale])
  }

  if (!response.ok) {
    let detail = `Request failed (HTTP ${response.status}).`
    try {
      const body = await response.json()
      if (body?.detail) detail = body.detail
    } catch {
      /* non-JSON error body */
    }
    throw new Error(detail)
  }

  return (await response.json()) as ChatReply
}

export async function fetchHealth(signal?: AbortSignal): Promise<Health | null> {
  try {
    const response = await fetch('/api/health', { signal })
    if (!response.ok) return null
    return (await response.json()) as Health
  } catch {
    return null
  }
}