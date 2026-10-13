import type { ReactNode } from 'react'

/**
 * Renders the small markdown subset the assistant produces — headings, bullet and numbered
 * lists, **bold**, and GFM pipe tables — without injecting HTML.
 */
export function RichText({ text }: { text: string }) {
  const blocks: ReactNode[] = []
  let bullets: string[] = []
  let numbers: string[] = []
  let table: string[][] = []

  const flushLists = () => {
    if (bullets.length) {
      blocks.push(<ul key={`ul-${blocks.length}`}>{bullets.map((b, i) => <li key={i}>{inline(b)}</li>)}</ul>)
      bullets = []
    }
    if (numbers.length) {
      blocks.push(<ol key={`ol-${blocks.length}`}>{numbers.map((b, i) => <li key={i}>{inline(b)}</li>)}</ol>)
      numbers = []
    }
  }

  const flushTable = () => {
    if (!table.length) return
    const [head, ...body] = table
    blocks.push(
      <div className="rich-table-wrap" key={`tb-${blocks.length}`}>
        <table className="rich-table">
          <thead><tr>{head.map((c, i) => <th key={i}>{inline(c)}</th>)}</tr></thead>
          <tbody>{body.map((row, r) => <tr key={r}>{row.map((c, i) => <td key={i}>{inline(c)}</td>)}</tr>)}</tbody>
        </table>
      </div>,
    )
    table = []
  }

  // | --- | :---: | style separator rows carry no data.
  const isSeparator = (line: string) => /^\|?[\s:|-]*-{2,}[\s:|-]*\|?$/.test(line) && line.includes('-')

  const cells = (line: string) =>
    line.replace(/^\s*\|/, '').replace(/\|\s*$/, '').split('|').map((c) => c.trim())

  for (const raw of text.split('\n')) {
    const line = raw.trim()

    if (line.startsWith('|')) {
      if (!isSeparator(line)) { flushLists(); table.push(cells(line)) }
      continue
    }
    if (table.length) flushTable()

    if (!line) { flushLists(); continue }

    const bullet = line.match(/^[*\-•]\s+(.*)$/)
    const numbered = line.match(/^\d+[.)]\s+(.*)$/)
    const heading = line.match(/^(#{1,6})\s+(.*)$/)

    if (bullet) { if (numbers.length) flushLists(); bullets.push(bullet[1]); continue }
    if (numbered) { if (bullets.length) flushLists(); numbers.push(numbered[1]); continue }
    flushLists()
    if (heading) blocks.push(<h4 key={blocks.length}>{inline(heading[2])}</h4>)
    else blocks.push(<p key={blocks.length}>{inline(line)}</p>)
  }
  flushLists()
  flushTable()
  return <div className="rich">{blocks}</div>
}

function inline(text: string): ReactNode[] {
  return text.split(/(\*\*[^*]+\*\*)/g).map((part, i) =>
    part.startsWith('**') && part.endsWith('**') ? <strong key={i}>{part.slice(2, -2)}</strong> : part,
  )
}
