/**
 * Renders an agent reply as HTML.
 *
 * Replies arrive as markdown — tables, bold, numbered lists — so rendering them
 * is what makes a contract table readable instead of a row of pipe characters.
 * That means `v-html`, which is only safe because of the sanitiser below.
 *
 * The text is not purely model-authored: party names and contract names come
 * straight out of SQL Server. A name containing `<script>` would otherwise
 * execute in the operator's browser, so everything is parsed as markdown and
 * then passed through DOMPurify before it is ever handed to the DOM. Do not
 * remove that call, and do not add a `sanitize: false` option.
 */
import DOMPurify from 'dompurify'
import { marked } from 'marked'

// Single-line replies are the common case; leaving this off makes marked emit
// loose <p> wrappers and adds vertical margin to every one-liner.
marked.setOptions({ gfm: true, breaks: true })

/**
 * DOMPurify strips `class` by default, which is fine here — no reply-supplied
 * class should survive anyway. `<a>` keeps `target`/`rel` off, so a link in a
 * reply navigates in place. Only http(s) and mailto survive; `javascript:` URLs
 * do not.
 */
const ALLOWED_TAGS = [
  'p', 'br', 'strong', 'em', 'del', 'code', 'pre', 'blockquote',
  'ul', 'ol', 'li',
  'h1', 'h2', 'h3', 'h4',
  'a', 'hr', 'br',
  'table', 'thead', 'tbody', 'tr', 'th', 'td',
]

export function renderMarkdown(source: string): string {
  const html = marked.parse(source, { async: false }) as string
  return DOMPurify.sanitize(html, { ALLOWED_TAGS, ALLOWED_ATTR: ['href', 'title'] })
}