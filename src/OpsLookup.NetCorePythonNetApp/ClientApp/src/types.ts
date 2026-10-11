/** Shape returned by the .NET endpoint, which proxies the Python graph. */
export interface ChatReply {
  threadId: string
  reply: string
  toolCalls: number
  /**
   * True when a tool ran but failed — e.g. the API rejected the token.
   * `toolCalls` counts invocations, not successes, so without this the UI
   * would badge a failed lookup as "read from the database".
   */
  lookupFailed?: boolean
}

/**
 * Body posted to /api/chat. `locale` selects the system prompt the agent
 * answers under, so the reply language matches the page.
 */
export interface ChatRequest {
  ticketId: string
  message: string
  locale?: 'ar' | 'en'
}

/** Body the ASP.NET Core host returns when the graph itself failed. */
export interface ChatFailure {
  detail: string
}

export interface Health {
  status: string
  pythonReady: boolean
  apiReachable: boolean
  startupError: string | null
}

export type ChatRole = 'member' | 'agent'

export interface Message {
  id: number
  role: ChatRole
  text: string
  /** Database lookups behind this reply. 0 means the answer was not verified. */
  toolCalls: number
  /** A lookup was attempted but failed. The answer is not verified. */
  lookupFailed?: boolean
  isError: boolean
}

export class ChatServiceError extends Error {}