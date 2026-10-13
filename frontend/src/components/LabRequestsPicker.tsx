// PLAN.md 10.10: the lab/radiology field in the visit form — a searchable multi-select fed by the
// owner's catalog (api/owner/catalog/items). Picked items show as removable chips; typing something
// that is not in the catalog can still be added as free text. Everything is saved as plain names
// joined by newlines, so old visits keep rendering exactly as before.
import { useEffect, useRef, useState } from 'react'
import { api } from '../api'
import { Icon } from './Icon'

export default function LabRequestsPicker({ value, onChange }: { value: string; onChange: (v: string) => void }) {
  const [chips, setChips] = useState<string[]>(() => value.split('\n').map((s) => s.trim()).filter(Boolean))
  const [q, setQ] = useState('')
  const [options, setOptions] = useState<{ id: number; name: string }[]>([])
  const [open, setOpen] = useState(false)
  const boxRef = useRef<HTMLDivElement>(null)

  useEffect(() => {
    const t = window.setTimeout(() => {
      api<{ id: number; name: string }[]>(`/api/catalog/items?q=${encodeURIComponent(q)}`)
        .then(setOptions)
        .catch(() => setOptions([]))
    }, 250)
    return () => window.clearTimeout(t)
  }, [q])

  useEffect(() => {
    const onDocClick = (e: MouseEvent) => {
      if (boxRef.current && !boxRef.current.contains(e.target as Node)) setOpen(false)
    }
    document.addEventListener('mousedown', onDocClick)
    return () => document.removeEventListener('mousedown', onDocClick)
  }, [])

  const emit = (next: string[]) => {
    setChips(next)
    onChange(next.join('\n'))
  }

  const add = (name: string) => {
    const clean = name.trim()
    if (!clean || chips.some((c) => c === clean)) return
    emit([...chips, clean])
    setQ('')
    setOpen(false) // the list folds right away after each pick (PLAN.md 10.10)
  }

  const shown = options.filter((o) => !chips.includes(o.name))
  const exact = q.trim() !== '' && !chips.includes(q.trim()) && !options.some((o) => o.name === q.trim())

  return (
    <div className="lab-picker" ref={boxRef}>
      {chips.length > 0 && (
        <div className="chips">
          {chips.map((c) => (
            <span key={c} className="chip">
              {c}
              <button type="button" className="chip-x" onClick={() => emit(chips.filter((x) => x !== c))} title="حذف">
                <Icon name="x" size={12} />
              </button>
            </span>
          ))}
        </div>
      )}
      <input
        className="input"
        value={q}
        placeholder="ابحث عن تحليل أو أشعة… أو اكتب اسماً حراً"
        onFocus={() => setOpen(true)}
        onChange={(e) => { setQ(e.target.value); setOpen(true) }}
        onKeyDown={(e) => { if (e.key === 'Enter') { e.preventDefault(); if (q.trim()) add(q) } }}
      />
      {open && (shown.length > 0 || exact) && (
        <div className="lab-picker-list">
          {shown.slice(0, 10).map((o) => (
            <button type="button" key={o.id} className="lab-picker-item" onMouseDown={() => add(o.name)}>
              {o.name}
            </button>
          ))}
          {exact && (
            <button type="button" className="lab-picker-item free" onMouseDown={() => add(q)}>
              <Icon name="plus" size={13} /> إضافة «{q.trim()}» كنص حر
            </button>
          )}
        </div>
      )}
    </div>
  )
}
