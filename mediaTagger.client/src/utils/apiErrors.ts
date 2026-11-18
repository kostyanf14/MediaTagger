import axios from 'axios'

interface ApiErrorBody { code?: string; message?: string; path?: string }

/** The error code the API sent along with a refusal, if there was one. */
export function apiErrorCode(error: unknown): string | undefined {
  if (!axios.isAxiosError(error)) return undefined
  const data = error.response?.data as ApiErrorBody | undefined
  return typeof data === 'object' && data ? data.code : undefined
}

/**
 * Says why a request failed in words the user can act on, for the failures that have
 * a known cause. Anything else gets <paramref name="fallback"/>.
 */
export function describeApiError(error: unknown, fallback: string): string {
  if (!axios.isAxiosError(error)) return fallback
  const data = error.response?.data as ApiErrorBody | undefined
  switch (apiErrorCode(error)) {
    case 'tags_unreadable':
      return 'Файл тегів у цій папці пошкоджений, тож правки не збережено. Поруч лежить копія _tags.json.bak.'
    case 'cross_volume':
      return 'Папку не можна перемістити на інший диск за один крок. Скопіюйте її туди вручну.'
    case 'outside_library':
      return 'Ця папка не входить до бібліотеки. Спершу додайте її.'
    case 'io':
      return data?.message ? `${fallback}: ${data.message}` : fallback
    default:
      return fallback
  }
}
