/**
 * Shape returned by the .NET endpoint.
 *
 * Identical to the Python agent's, so the same UI drives either.
 */
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

/** Body the ASP.NET Core host returns when the turn itself failed. */
export interface ChatFailure {
  detail?: string
  error?: string
}

export interface Health {
  status: string
  /**
   * True when the agent can serve a turn.
   *
   * The Python agent reported `pythonReady`, meaning "the embedded interpreter
   * finished warming". This one is pure C#, so it is ready as soon as the
   * process is, and the useful distinction is `apiReachable` -- a service that
   * is up but cannot reach the data API cannot answer anything.
   */
  ready: boolean
  apiReachable: boolean
  /** False when no model key is configured; the turn will fail with that named. */
  modelConfigured?: boolean
  agent?: string
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