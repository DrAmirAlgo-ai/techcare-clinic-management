import { useCallback, useEffect, useRef, useState } from 'react'
import { api, errorMessage } from '../api'
import { Icon } from '../components/Icon'
import { Modal } from '../components/Modal'
import { fmtShortDate } from '../format'

interface LibraryData {
  folder: string
  documents: { id: number; fileName: string; title: string; fileSize: number; chunkCount: number; indexedAtUtc: string }[]
  notIndexed: string[]
  status: { isRunning: boolean; totalFiles: number; processedFiles: number; currentFile?: string; errors: string[]; lastRunUtc?: string }
}

export function Library() {
  const [data, setData] = useState<LibraryData | null>(null)
  const [error, setError] = useState('')
  const [uploading, setUploading] = useState(false)
  const [deleting, setDeleting] = useState<{ fileName: string; title: string } | null>(null)
  const [busyDelete, setBusyDelete] = useState(false)
  const fileInput = useRef<HTMLInputElement>(null)

  const load = useCallback(() => {
    api<LibraryData>('/api/library').then(setData).catch((e) => setError(errorMessage(e)))
  }, [])
  useEffect(load, [load])

  useEffect(() => {
    if (!data?.status.isRunning) return
    const t = window.setTimeout(load, 2000)
    return () => window.clearTimeout(t)
  }, [data, load])

  async function reindex() {
    setError('')
    try {
      await api('/api/library/reindex', { method: 'POST', body: {} })
      load()
    } catch (e) {
      setError(errorMessage(e))
    }
  }

  async function remove() {
    if (!deleting) return
    setBusyDelete(true)
    setError('')
    try {
      await api(`/api/library?name=${encodeURIComponent(deleting.fileName)}`, { method: 'DELETE' })
      setDeleting(null)
      load()
    } catch (e) {
      setError(errorMessage(e))
      setDeleting(null)
    } finally {
      setBusyDelete(false)
    }
  }

  async function upload(files: FileList | null) {
    if (!files?.length) return
    setUploading(true)
    setError('')
    try {
      for (const file of Array.from(files)) {
        const form = new FormData()
        form.append('file', file)
        await api('/api/library/upload', { form })
      }
      load()
    } catch (e) {
      setError(errorMessage(e))
    } finally {
      setUploading(false)
      if (fileInput.current) fileInput.current.value = ''
    }
  }

  const s = data?.status
  return (
    <>
      <div className="page-head">
        <h1>المكتبة الطبية</h1>
        <div className="row">
          <input ref={fileInput} type="file" accept=".pdf,.txt,.md,.jsonl" multiple hidden onChange={(e) => upload(e.target.files)} />
          <button className="btn ghost" onClick={reindex} disabled={s?.isRunning}><Icon name="refresh" /> إعادة الفهرسة</button>
          <button className="btn" onClick={() => fileInput.current?.click()} disabled={uploading}>{uploading ? <span className="spinner" /> : <Icon name="upload" />} رفع مراجع</button>
        </div>
      </div>
      {error && <div className="alert error page-alert"><Icon name="alert" /> {error}</div>}
      {s?.isRunning && (
        <div className="alert info page-alert">
          <span className="spinner dark" /> جارٍ الفهرسة {s.processedFiles} / {s.totalFiles}{s.currentFile ? ` · ${s.currentFile}` : ''}
        </div>
      )}
      {s && s.errors.length > 0 && (
        <div className="alert error page-alert" style={{ flexDirection: 'column', alignItems: 'flex-start' }}>
          {s.errors.map((e, i) => <div key={i}>{e}</div>)}
        </div>
      )}
      {data && data.notIndexed.length > 0 && !s?.isRunning && (
        <div className="alert warn page-alert" style={{ flexDirection: 'column', alignItems: 'stretch' }}>
          <div className="row tight"><Icon name="alert" /> ملفات غير مفهرسة</div>
          {data.notIndexed.map((n) => (
            <div key={n} className="row between">
              <span dir="auto">{n}</span>
              <button className="btn sm danger" onClick={() => setDeleting({ fileName: n, title: n })}><Icon name="x" size={14} /> حذف</button>
            </div>
          ))}
        </div>
      )}
      <div className="card flush">
        <div className="card-head">
          <div className="card-title"><Icon name="book" /><h3>المراجع</h3><span className="count-pill">{data?.documents.length ?? 0}</span></div>
        </div>
        {!data ? <div className="loading"><span className="spinner dark lg" /></div> : data.documents.length === 0 ? (
          <div className="empty"><Icon name="book" size={30} /> لا توجد مراجع</div>
        ) : (
          <div className="table-wrap">
            <table className="table">
              <thead><tr><th>المرجع</th><th>الحجم</th><th>الأجزاء المفهرسة</th><th>تاريخ الفهرسة</th><th></th></tr></thead>
              <tbody>
                {data.documents.map((d) => (
                  <tr key={d.id}>
                    <td><div className="row tight"><Icon name="file" size={16} className="muted" /><span className="cell-main" dir="auto">{d.title}</span></div></td>
                    <td className="nowrap">{(d.fileSize / 1024 / 1024).toFixed(1)} MB</td>
                    <td>{d.chunkCount}</td>
                    <td className="nowrap">{fmtShortDate(d.indexedAtUtc)}</td>
                    <td>
                      <div className="actions">
                        <button className="icon-btn light" style={{ color: 'var(--red)' }} title="حذف" disabled={s?.isRunning} onClick={() => setDeleting({ fileName: d.fileName, title: d.title })}>
                          <Icon name="x" size={16} />
                        </button>
                      </div>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>
      {deleting && (
        <Modal title="حذف مرجع" onClose={() => setDeleting(null)}>
          <div className="stack" style={{ gap: 18 }}>
            <div>هل تريد حذف <b dir="auto">{deleting.title}</b> نهائياً من المكتبة؟</div>
            <div className="row end">
              <button className="btn ghost" onClick={() => setDeleting(null)}>إلغاء</button>
              <button className="btn" style={{ background: 'var(--red)', borderColor: 'var(--red)' }} onClick={remove} disabled={busyDelete}>
                {busyDelete ? <span className="spinner" /> : <Icon name="x" />} حذف
              </button>
            </div>
          </div>
        </Modal>
      )}
    </>
  )
}
