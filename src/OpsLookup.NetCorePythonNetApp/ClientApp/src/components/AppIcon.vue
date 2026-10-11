/**
 * Inline SVG icons, stroked to match `currentColor` at 1.6px.
 *
 * Kept in-project rather than pulled from an icon package so the built page has
 * no external dependency and makes no extra request. Every icon is
 * `aria-hidden`: each one sits beside a real text label, so announcing it would
 * only add noise for a screen reader.
 */
<script setup lang="ts">
export type IconName =
  | 'database'
  | 'send'
  | 'sun'
  | 'moon'
  | 'monitor'
  | 'alert'
  | 'refresh'
  | 'sparkle'
  | 'user'
  | 'shield'
  | 'plus'

defineProps<{ name: IconName; class?: string }>()
</script>

<template>
  <svg
    xmlns="http://www.w3.org/2000/svg"
    viewBox="0 0 24 24"
    fill="none"
    stroke="currentColor"
    stroke-width="1.6"
    stroke-linecap="round"
    stroke-linejoin="round"
    aria-hidden="true"
    focusable="false"
    :class="$props.class"
  >
    <!-- Database -->
    <template v-if="name === 'database'">
      <ellipse cx="12" cy="5.5" rx="7.5" ry="3" />
      <path d="M4.5 5.5v6c0 1.66 3.36 3 7.5 3s7.5-1.34 7.5-3v-6" />
      <path d="M4.5 11.5v6c0 1.66 3.36 3 7.5 3s7.5-1.34 7.5-3v-6" />
    </template>

    <!-- Send: paper plane, tilted -->
    <template v-else-if="name === 'send'">
      <path d="M20.5 3.5 10.8 13.2" />
      <path d="M20.5 3.5 14.3 20.5l-3.5-7.3-7.3-3.5Z" />
    </template>

    <template v-else-if="name === 'sun'">
      <circle cx="12" cy="12" r="4" />
      <path d="M12 2.5v2M12 19.5v2M2.5 12h2M19.5 12h2M5.3 5.3l1.4 1.4M17.3 17.3l1.4 1.4M18.7 5.3l-1.4 1.4M6.7 17.3l-1.4 1.4" />
    </template>

    <template v-else-if="name === 'moon'">
      <path d="M20 14.2A8.2 8.2 0 0 1 9.8 4 8.5 8.5 0 1 0 20 14.2Z" />
    </template>

    <template v-else-if="name === 'monitor'">
      <rect x="2.5" y="4" width="19" height="12.5" rx="2" />
      <path d="M9 20.5h6M12 16.5v4" />
    </template>

    <template v-else-if="name === 'alert'">
      <path d="M10.3 3.9 2.4 17.5A2 2 0 0 0 4.1 20.5h15.8a2 2 0 0 0 1.7-3L13.7 3.9a2 2 0 0 0-3.4 0Z" />
      <path d="M12 9.5v4M12 17h.01" />
    </template>

    <template v-else-if="name === 'refresh'">
      <path d="M20.5 11.5a8.5 8.5 0 1 0-1.9 6.2" />
      <path d="M20.5 5.5v6h-6" />
    </template>

    <template v-else-if="name === 'sparkle'">
      <path d="M12 3.5 13.9 9l5.6 1.9-5.6 1.9L12 18.5 10.1 12.8 4.5 10.9 10.1 9Z" />
      <path d="M18.5 3v3M20 4.5h-3" />
    </template>

    <template v-else-if="name === 'user'">
      <circle cx="12" cy="8" r="3.75" />
      <path d="M4.5 20.5a7.5 7.5 0 0 1 15 0" />
    </template>

    <template v-else-if="name === 'shield'">
      <path d="M12 2.8 4.5 6v6c0 4.4 3.1 8.2 7.5 9.3 4.4-1.1 7.5-4.9 7.5-9.3V6Z" />
      <path d="m9 12 2.2 2.2L15.5 10" />
    </template>

    <template v-else-if="name === 'plus'">
      <path d="M12 5v14M5 12h14" />
    </template>
  </svg>
</template>