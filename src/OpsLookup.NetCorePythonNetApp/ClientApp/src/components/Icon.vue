/**
 * Inline SVG icons, stroked to match `currentColor` at 1.5px.
 *
 * Kept as components rather than an icon package so the built page has no
 * external dependency and no extra request. `aria-hidden` throughout: each icon
 * sits next to a real label, so announcing it would only add noise.
 */
<script setup lang="ts">
defineProps<{ class?: string }>()
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
    :class="$props.class"
  >
    <slot />
  </svg>
</template>

<template id="none"></template>

<script lang="ts">
// Named export used via <Icon name="..."> would need a dynamic component map.
// Individual components below are clearer at the call site.
export { default as Icon } from './Icon.vue'
</script>