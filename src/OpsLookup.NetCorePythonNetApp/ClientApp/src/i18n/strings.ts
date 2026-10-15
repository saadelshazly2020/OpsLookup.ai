/**
 * Translations.
 *
 * Arabic is the default. `document.documentElement.lang` and `dir` are driven
 * from here, so choosing a language also flips the layout to RTL — see
 * `useI18n.ts`.
 *
 * Two things deliberately stay untranslated:
 *   - `toolCalls` values. A social ID must be read back exactly as stored.
 *   - Placeholder data (names, contract titles). Those come from the database
 *     and are not ours to translate.
 *
 * Agent replies follow the selected language: the UI passes `language` to
 * /api/chat and the graph picks the matching system prompt, so the page does
 * not end up reading as two languages at once.
 */
export const LOCALES = ['ar', 'en'] as const
export type Locale = (typeof LOCALES)[number]

export const LOCALE_INFO: Record<Locale, { label: string; dir: 'rtl' | 'ltr' }> = {
  ar: { label: 'العربية', dir: 'rtl' },
  en: { label: 'English', dir: 'ltr' },
}

export const strings = {
  ar: {
    appName: 'مساعد التذاكر الذكي',
    tagline: 'اسأل بالعربية عن عقود العميل ووكالته.',
    newConversation: 'محادثة جديدة',
    loading: 'جارٍ التحميل…',

    ready: 'جاهز',
    starting: 'جارٍ تشغيل الوكيل…',
    apiUnreachable: 'واجهة البيانات غير متاحة',
    apiUnreachableHint: 'شغّل OpsLookup.Api على المنفذ 5250.',

    startTitle: 'ابدأ البحث',
    startBody:
      'صِف البلاغ بكلماتك. أضف الرقم الوطني الموحد إن كان لديك — يجب أن يكون عشرة أرقام يبدأ بالرقم 1 أو 2.',
    example1Label: 'العقود لرقم وطني موحد',
    example1Text: 'ما العقود المسجلة للرقم الوطني الموحد 1234567890؟',
    example2Label: 'البحث عن وكالة',
    example2Text: 'من هو وكيل الرقم الوطني الموحد 2111111111، وهل وكالته معتمدة؟',
    example3Label: 'البحث باسم العميل',
    example3Text: 'ابحث عن العميل المسمى Karimi وأخبرني بما لديه.',

    placeholder: 'اكتب وصف البلاغ بكلماتك…',
    composerHint: 'Enter للإرسال · Shift + Enter لسطر جديد',
    composerLabel: 'وصف البلاغ',
    send: 'إرسال',
    sending: 'جارٍ انتظار الوكيل',

    you: 'أنت',
    agent: 'مساعد التذاكر',
    problem: 'خطأ',
    somethingWentWrong: 'حدث خطأ ما.',

    ticket: 'رقم البلاغ',
    statusVerified: 'تمت الإجابة من قاعدة البيانات.',
    statusUnverified: 'تمت الإجابة دون الرجوع لقاعدة البيانات.',
    statusFailed: 'فشل البحث — لم تتم قراءة أي بيانات.',

    badgeOk: 'تم البحث {n} مرة في قاعدة البيانات',
    badgeOkOnce: 'تم البحث مرة واحدة في قاعدة البيانات',
    badgeOkTitle: 'هذه الإجابة مقروءة من قاعدة البيانات.',
    badgeNone: 'لم يتم التحقق من قاعدة البيانات',
    badgeNoneTitle: 'لم يتم أي بحث في قاعدة البيانات، لذلك لم تتم التحقق من هذه الإجابة.',
    badgeFailed: 'فشل البحث — غير مُتحقق منه',
    badgeFailedTitle: 'تم تشغيل الأداة لكنها لم تصل إلى البيانات. لم تتم قراءة أي شيء.',

    themeLabel: 'مظهر الصفحة',
    themeLight: 'فاتح',
    themeSystem: 'تلقائي',
    themeDark: 'داكن',

    conversationLabel: 'المحادثة',
    errorAlert: 'خطأ',
  },

  en: {
    appName: 'Ticketing AI Assistant',
    tagline: 'Ask about a party’s contracts and attorney in plain language.',
    newConversation: 'New conversation',
    loading: 'Loading…',

    ready: 'Ready',
    starting: 'Agent starting…',
    apiUnreachable: 'Data API unreachable',
    apiUnreachableHint: 'Start OpsLookup.Api on port 5250.',

    startTitle: 'Start a lookup',
    startBody:
      'Describe the ticket in your own words. Include the social ID if you have it — it must be 10 digits starting with 1 or 2.',
    example1Label: 'Contracts for a social ID',
    example1Text: 'What contracts are held for social ID 1234567890?',
    example2Label: 'Find an attorney',
    example2Text: 'Who is the attorney for social ID 2111111111, and are they approved?',
    example3Label: 'Search by party name',
    example3Text: 'Look up the party named Karimi and tell me what they hold.',

    placeholder: 'Describe the ticket in your own words…',
    composerHint: 'Enter to send · Shift + Enter for a new line',
    composerLabel: 'Describe the ticket',
    send: 'Send',
    sending: 'Waiting for the agent',

    you: 'You',
    agent: 'Ticketing AI Assistant',
    problem: 'Problem',
    somethingWentWrong: 'Something went wrong.',

    ticket: 'Ticket',
    statusVerified: 'Answered from a database lookup.',
    statusUnverified: 'Answered without a database lookup.',
    statusFailed: 'The lookup failed — nothing was read.',

    badgeOk: 'Looked up {n} times in the database',
    badgeOkOnce: 'Looked up 1 time in the database',
    badgeOkTitle: 'This answer was read from the database.',
    badgeNone: 'Not checked against the database',
    badgeNoneTitle: 'No database lookup was made, so this answer was not verified.',
    badgeFailed: 'Lookup failed — not verified',
    badgeFailedTitle: 'A tool ran but could not reach the data. Nothing was read from the database.',

    themeLabel: 'Colour theme',
    themeLight: 'Light',
    themeSystem: 'System',
    themeDark: 'Dark',

    conversationLabel: 'Conversation',
    errorAlert: 'Error',
  },
} as const

export type Strings = (typeof strings)['en']