<script setup lang="ts">
import type { Message } from '../types'

defineProps<{ message: Message }>()
</script>

<template>
  <div class="msg" :class="[message.role, { error: message.isError }]">
    <div class="who">
      {{ message.role === 'member' ? 'You' : message.isError ? 'Problem' : 'Agent' }}
    </div>

    <div class="bubble">{{ message.text }}</div>

    <!--
      The badge is the point of the whole app. Without it a member cannot tell
      an answer read from SQL Server from one the model invented, so an answer
      with no badge was NOT verified against data.
    -->
    <span
      v-if="message.role === 'agent' && !message.isError && message.toolCalls > 0"
      class="badge"
    >
      Looked up {{ message.toolCalls }} time{{ message.toolCalls === 1 ? '' : 's' }} in the database
    </span>
  </div>
</template>

<style scoped>
.msg {
  max-width: 720px;
  width: 100%;
  margin: 0 auto;
}

.who {
  font-size: 11px;
  text-transform: uppercase;
  letter-spacing: 0.06em;
  color: var(--muted);
  margin-bottom: 5px;
}

.bubble {
  background: var(--agent-bubble);
  border: 1px solid var(--border);
  border-radius: var(--radius);
  padding: 11px 14px;
  white-space: pre-wrap;
  overflow-wrap: anywhere;
}

.member {
  align-self: flex-end;
}

.member .who {
  text-align: right;
}

.member .bubble {
  background: var(--accent);
  border-color: transparent;
  color: #fff;
}

.error .bubble {
  background: var(--error-soft);
  border-color: var(--error);
  color: var(--error);
}

.badge {
  display: inline-block;
  margin-top: 8px;
  font-size: 12px;
  color: var(--muted);
  background: var(--accent-soft);
  border-radius: 999px;
  padding: 2px 10px;
}
</style>