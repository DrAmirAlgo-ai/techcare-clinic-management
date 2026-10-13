import { useEffect, useRef, useState } from 'react'
import { Icon } from './Icon'

// PLAN_FULL.md 5.2 — dictation through the Web Speech API, with a plain-text fallback: on a browser without
// SpeechRecognition, or when the microphone is refused, the button explains it in Arabic and the field stays
// typeable. Interim results flow into the field live; a pause keeps the final text already written.
interface RecResult {
  isFinal: boolean
  0: { transcript: string }
}

interface Recognizer {
  lang: string
  continuous: boolean
  interimResults: boolean
  onresult: ((e: { resultIndex: number; results: RecResult[] }) => void) | null
  onerror: ((e: { error: string }) => void) | null
  onend: (() => void) | null
  start(): void
  stop(): void
}

function createRecognizer(): Recognizer | null {
  const w = window as unknown as {
    SpeechRecognition?: new () => Recognizer
    webkitSpeechRecognition?: new () => Recognizer
  }
  const Ctor = w.SpeechRecognition ?? w.webkitSpeechRecognition
  return Ctor ? new Ctor() : null
}

export function VoiceInput({ value, onChange }: { value: string; onChange: (text: string) => void }) {
  const [listening, setListening] = useState(false)
  const [note, setNote] = useState('')
  const recRef = useRef<Recognizer | null>(null)
  const baseRef = useRef('')

  // Releasing the microphone when the component goes away is the only way the mic light turns off.
  useEffect(() => () => { recRef.current?.stop() }, [])

  function toggle() {
    if (listening) {
      recRef.current?.stop()
      return
    }
    setNote('')
    const rec = createRecognizer()
    if (!rec) {
      setNote('المتصفح لا يدعم الإملاء الصوتي — اكتب في الحقل مباشرة')
      return
    }

    baseRef.current = value.trim()
    rec.lang = 'ar-EG'
    rec.continuous = true
    rec.interimResults = true
    rec.onresult = (e) => {
      let final = ''
      let interim = ''
      for (let i = e.resultIndex; i < e.results.length; i++) {
        const r = e.results[i]
        if (r.isFinal) final += r[0].transcript
        else interim += r[0].transcript
      }
      onChange([baseRef.current, (final + interim).trim()].filter(Boolean).join(' '))
    }
    rec.onerror = (e) => {
      if (e.error === 'not-allowed' || e.error === 'service-not-allowed')
        setNote('لم يُمنح إذن الميكروفون — اكتب في الحقل مباشرة')
      else if (e.error !== 'no-speech' && e.error !== 'aborted')
        setNote('تعذّر الاستماع للصوت — حاول مجدداً أو اكتب يدوياً')
    }
    rec.onend = () => {
      recRef.current = null
      setListening(false)
    }
    recRef.current = rec
    setListening(true)
    try {
      rec.start()
    } catch {
      recRef.current = null
      setListening(false)
      setNote('تعذّر تشغيل الميكروفون — اكتب في الحقل مباشرة')
    }
  }

  return (
    <div className="row tight" style={{ marginTop: 4, gap: 6, minWidth: 0 }}>
      <button type="button" className="icon-btn light" style={{ width: 26, height: 26, flexShrink: 0 }}
        onClick={toggle} title={listening ? 'إيقاف الإملاء' : 'إملاء صوتي'}>
        <Icon name="mic" size={14} />
      </button>
      {listening && <span className="badge red" style={{ fontSize: 11, flexShrink: 0 }}>جارٍ الاستماع…</span>}
      {note && !listening && <span className="muted small">{note}</span>}
    </div>
  )
}
