/**
 * POST /api/chat.
 *
 * A 502 carries the reason the model call failed; a 503 means the embedded
 * Python graph is still starting. Both are surfaced as readable text rather
 * than a raw status code.
 */
export async function sendChat(
  ticketId: string,
  message: string,
  signal?: AbortSignal,
): Promise<import('../types').ChatReply> {
  let response: Response
  try {
    response = await fetch('/api/chat', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ ticketId, message }),
      signal,
    })
  } catch {
    throw new Error('Could not reach the agent. Check that the .NET app is running.')
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

  return (await response.json()) as import('../types').ChatReply
}

export async function fetchHealth(
  signal?: AbortSignal,
): Promise<import('../types').Health | null> {
  try {
    const response = await fetch('/api/health', { signal })
    if (!response.ok) return null
    return (await response.json()) as import('../types').Health
  } catch {
    return null
  }
}