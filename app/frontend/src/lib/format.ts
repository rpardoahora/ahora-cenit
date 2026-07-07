const BYTE_UNITS = ["B", "KB", "MB", "GB", "TB"]

export function formatBytes(bytes: number): string {
  if (bytes <= 0) return "0 B"

  let value = bytes
  let unitIndex = 0
  while (value >= 1024 && unitIndex < BYTE_UNITS.length - 1) {
    value /= 1024
    unitIndex++
  }

  const decimals = unitIndex === 0 ? 0 : value < 10 ? 1 : 0
  return `${value.toFixed(decimals)} ${BYTE_UNITS[unitIndex]}`
}

export function formatPercent(value: number): string {
  return `${value < 10 ? value.toFixed(1) : Math.round(value)}%`
}
