/** Shape returned by the .NET endpoint, which proxies the Python graph. */
export interface ChatReply {
  threadId: string
  reply: string
  toolCalls: number
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
  isError: boolean
}

export class ChatServiceError extends Error {}