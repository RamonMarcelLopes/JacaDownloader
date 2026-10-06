'use client'

import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import {
  AlertCircle,
  Check,
  ChevronDown,
  Copy,
  Download,
  ExternalLink,
  FolderOpen,
  History,
  Link2,
  LoaderCircle,
  MoreHorizontal,
  Music2,
  Play,
  RotateCcw,
  Search,
  Settings as SettingsIcon,
  SlidersHorizontal,
  Sparkles,
  Trash2,
  Video,
  X,
} from 'lucide-react'
import {
  api,
  copyText,
  fileNameOf,
  formatDate,
  formatDuration,
  formatLabel,
  formatSize,
  hostMessage,
  platformLabel,
  platformLabels,
  type Entry,
  type Info,
  type Settings,
  type Tab,
  type Theme,
  type ToolStatus,
} from '@/lib/api'

type Option = { value: string; label: string }
type ConfirmOptions = { title: string; text: string; confirmLabel: string; danger?: boolean; onConfirm: () => void }
type ModalState = ({ kind: 'confirm' } & ConfirmOptions) | { kind: 'format'; entry: Entry }

const browserOptions: Option[] = [
  { value: '', label: 'Não usar' },
  { value: 'chrome', label: 'Chrome' },
  { value: 'edge', label: 'Edge' },
  { value: 'firefox', label: 'Firefox' },
  { value: 'brave', label: 'Brave' },
]

const qualityPresets: Option[] = [
  { value: '0', label: 'Melhor disponível' },
  { value: '1080', label: '1080p' },
  { value: '720', label: '720p' },
  { value: '480', label: '480p' },
  { value: '360', label: '360p' },
]

const retentionOptions: Option[] = [
  { value: '0', label: 'Sempre' },
  { value: '30', label: '30 dias' },
  { value: '90', label: '90 dias' },
  { value: '-1', label: 'Não guardar' },
]

function errorText(error: unknown) {
  return error instanceof Error ? error.message : 'Algo deu errado. Tente de novo.'
}

function Logo({ small = false }: { small?: boolean }) {
  return <img src="/logo.png" alt="Jaca Downloader" className={small ? 'brand-mark small' : 'brand-mark'} />
}

function Badge({ children, tone = 'neutral' }: { children: React.ReactNode; tone?: string }) {
  return <span className={`badge ${tone}`}>{children}</span>
}

function MenuSelect({ label, value, options, onChange, disabled }: { label: string; value: string; options: Option[]; onChange: (value: string) => void; disabled?: boolean }) {
  const [open, setOpen] = useState(false)
  const ref = useRef<HTMLDivElement>(null)

  useEffect(() => {
    if (!open) return
    const close = (event: MouseEvent) => { if (!ref.current?.contains(event.target as Node)) setOpen(false) }
    const escape = (event: KeyboardEvent) => { if (event.key === 'Escape') setOpen(false) }
    document.addEventListener('mousedown', close)
    document.addEventListener('keydown', escape)
    return () => { document.removeEventListener('mousedown', close); document.removeEventListener('keydown', escape) }
  }, [open])

  const current = options.find((option) => option.value === value)
  return <div ref={ref} className={`menu-select ${open ? 'open' : ''}`}>
    <button type="button" disabled={disabled} aria-haspopup="listbox" aria-expanded={open} aria-label={label} onClick={() => setOpen(!open)}>{current?.label ?? value}<ChevronDown size={15} /></button>
    {open && <div className="menu-options" role="listbox" aria-label={label}>{options.map((option) => <button type="button" role="option" aria-selected={option.value === value} key={option.value} onClick={() => { onChange(option.value); setOpen(false) }}>{option.label}</button>)}</div>}
  </div>
}

function WindowControls() {
  return <div className="window-controls" aria-label="Controles da janela">
    <button className="window-minimize" aria-label="Minimizar" title="Minimizar" onClick={() => hostMessage('minimize')} />
    <button className="window-maximize" aria-label="Tela cheia (indisponível)" title="Tela cheia indisponível" disabled />
    <button className="window-close" aria-label="Fechar" title="Fechar" onClick={() => hostMessage('close')} />
  </div>
}

function Modal({ title, onClose, children }: { title: string; onClose: () => void; children: React.ReactNode }) {
  useEffect(() => {
    const escape = (event: KeyboardEvent) => { if (event.key === 'Escape') onClose() }
    document.addEventListener('keydown', escape)
    return () => document.removeEventListener('keydown', escape)
  }, [onClose])

  return <div className="modal-backdrop" onMouseDown={(event) => { if (event.target === event.currentTarget) onClose() }}>
    <div className="modal card" role="dialog" aria-modal="true" aria-label={title}>
      <div className="modal-head"><h2>{title}</h2><button className="icon-button" aria-label="Fechar" onClick={onClose}><X size={18} /></button></div>
      {children}
    </div>
  </div>
}

function DownloadPage({ settings, ready, history, request, onStarted, onOpenHistory, toast }: {
  settings: Settings
  ready: boolean
  history: Entry[]
  request?: { url: string; nonce: number }
  onStarted: (entry: Entry) => void
  onOpenHistory: () => void
  toast: (message: string) => void
}) {
  const [link, setLink] = useState('')
  const [info, setInfo] = useState<Info | null>(null)
  const [analyzing, setAnalyzing] = useState(false)
  const [analyzeError, setAnalyzeError] = useState('')
  const [type, setType] = useState<'video' | 'audio'>('video')
  const [quality, setQuality] = useState('0')
  const [dir, setDir] = useState<string | null>(null)
  const [cookies, setCookies] = useState<string | null>(null)
  const [advancedOpen, setAdvancedOpen] = useState(false)
  const [jobId, setJobId] = useState<string | null>(null)
  const [startError, setStartError] = useState('')

  const folder = dir ?? settings.defaultDir
  const browser = cookies ?? settings.cookies
  const job = jobId ? history.find((entry) => entry.id === jobId) : undefined
  const running = job?.status === 'downloading'
  const audioOnly = !!info && (info.spotify || info.heights.length === 0)

  async function analyze(target = link) {
    const url = target.trim()
    if (!url || analyzing || !ready) return
    setAnalyzing(true)
    setAnalyzeError('')
    try {
      const result = await api<Info>('/api/info', { url, cookies: browser })
      setInfo(result)
      setJobId(null)
      setStartError('')
      setType(result.spotify || result.heights.length === 0 ? 'audio' : settings.defaultType)
      const preferred = settings.defaultQuality
      const pick = preferred > 0 ? result.heights.find((height) => height <= preferred) : undefined
      setQuality(String(pick ?? 0))
    } catch (error) {
      setInfo(null)
      setAnalyzeError(errorText(error))
    } finally {
      setAnalyzing(false)
    }
  }

  const analyzeRef = useRef(analyze)
  analyzeRef.current = analyze
  useEffect(() => {
    if (!request) return
    setLink(request.url)
    analyzeRef.current(request.url)
  }, [request])

  function touch() {
    if (job && job.status !== 'downloading') setJobId(null)
    setStartError('')
  }

  function reset() {
    setInfo(null)
    setJobId(null)
    setStartError('')
    setAnalyzeError('')
  }

  async function chooseFolder() {
    try {
      const result = await api<{ path: string | null }>('/api/pick-folder', { initial: folder })
      if (result.path) { setDir(result.path); touch() }
    } catch (error) { toast(errorText(error)) }
  }

  async function startDownload() {
    if (!info) return
    setStartError('')
    try {
      const entry = await api<Entry>('/api/download', {
        url: link.trim(),
        kind: type,
        height: Number(quality) || 0,
        outputDir: folder,
        cookies: browser,
      })
      setJobId(entry.id)
      onStarted(entry)
      toast('Download iniciado')
    } catch (error) {
      setStartError(errorText(error))
    }
  }

  async function retry() {
    if (!job) return
    try {
      const entry = await api<Entry>(`/api/history/${job.id}/retry`, {})
      setJobId(entry.id)
      onStarted(entry)
    } catch (error) { setStartError(errorText(error)) }
  }

  async function showInFolder() {
    if (!job) return
    try { await api(`/api/history/${job.id}/show`, {}) } catch (error) { toast(errorText(error)) }
  }

  const qualityOptions: Option[] = info
    ? [{ value: '0', label: 'Melhor disponível' }, ...info.heights.map((height) => ({ value: String(height), label: `${height}p` }))]
    : []
  const needsLogin = /login|idade|privad/i.test(analyzeError)

  return (
    <div className="page-stack">
      <section className="hero-copy">
        <div>
          <p className="eyebrow"><Download size={14} /> download</p>
          <h1>Baixar arquivo</h1>
          <p className="hero-subtitle">Insira um link e selecione o formato do arquivo.</p>
        </div>
        <div className="hero-orb"><Download size={32} strokeWidth={1.8} /></div>
      </section>

      <section className="card link-card">
        <div className="section-heading"><div><h2>Comece por aqui</h2><p>Funciona com YouTube, TikTok, Instagram, X e Spotify.</p></div><Link2 className="heading-icon" size={20} /></div>
        <div className="input-row">
          <div className="input-wrap"><Link2 size={18} /><input aria-label="Link do vídeo ou música" value={link} disabled={analyzing} onChange={(event) => setLink(event.target.value)} onKeyDown={(event) => { if (event.key === 'Enter') analyze() }} placeholder="Cole o link do vídeo ou da música" /></div>
          <button className="primary-button" onClick={() => analyze()} disabled={!link.trim() || analyzing || !ready}>{analyzing ? <LoaderCircle className="spin" size={17} /> : <Search size={17} />} {analyzing ? 'Analisando...' : 'Analisar'}</button>
        </div>
        <div className="source-list"><span>Fontes compatíveis</span><Badge>YouTube</Badge><Badge>TikTok</Badge><Badge>Instagram</Badge><Badge>X</Badge><Badge>Spotify</Badge></div>
        {analyzeError && <p className="error-line"><AlertCircle size={14} /> {analyzeError}</p>}
        {analyzeError && needsLogin && <p className="field-hint hint-gap">Se o post pede login, escolha seu navegador em Opções avançadas depois de analisar, ou em Configurações.</p>}
      </section>

      {info ? <section className="card options-card">
        <div className="preview-row">
          <div className="video-thumb" style={info.thumbnail ? { backgroundImage: `linear-gradient(180deg, #0001, #0009), url("${info.thumbnail}")` } : undefined} aria-label="Thumbnail do vídeo"><Play size={24} fill="white" /></div>
          <div className="preview-copy"><Badge tone={info.platform === 'youtube' ? 'youtube' : 'neutral'}>{platformLabel(info.platform).toUpperCase()}</Badge><h3>{info.title ?? link}</h3><p>{[info.uploader, formatDuration(info.duration)].filter(Boolean).join(' · ')}</p></div>
          <button className="icon-button" aria-label="Remover análise" onClick={reset}><X size={18} /></button>
        </div>
        <div className="divider" />
        <div className="field-label">O que você quer baixar?</div>
        <div className="segmented">
          <button className={type === 'video' ? 'selected' : ''} disabled={audioOnly} onClick={() => { setType('video'); touch() }}><Video size={17} /> Vídeo <span>MP4</span></button>
          <button className={type === 'audio' ? 'selected' : ''} onClick={() => { setType('audio'); touch() }}><Music2 size={17} /> Áudio <span>MP3</span></button>
        </div>
        {type === 'video' && <div className="field"><label>Qualidade do vídeo</label><div className="select-wrap"><MenuSelect label="Qualidade do vídeo" value={quality} options={qualityOptions} onChange={(value) => { setQuality(value); touch() }} /></div><p className="field-hint">Mostramos só as qualidades que existem para este vídeo.</p></div>}
        <div className="field"><label htmlFor="folder">Salvar em</label><div className="input-wrap"><FolderOpen size={17} /><input id="folder" value={folder} onChange={(event) => { setDir(event.target.value); touch() }} /><button className="inline-action" onClick={chooseFolder}>Escolher</button></div></div>
        <button className="advanced-row" aria-expanded={advancedOpen} onClick={() => setAdvancedOpen(!advancedOpen)}><SlidersHorizontal size={16} /> Opções avançadas <ChevronDown size={16} className={advancedOpen ? 'flip' : ''} /></button>
        {advancedOpen && <div className="field advanced-panel"><label>Usar login do navegador</label><div className="select-wrap"><MenuSelect label="Usar login do navegador" value={browser} options={browserOptions} onChange={(value) => { setCookies(value); touch() }} /></div><p className="field-hint">Ajuda em posts que pedem login, como Instagram e X. Escolha o navegador em que você está logado.</p></div>}
        {info.spotify && <div className="info-note"><Music2 size={17} /><span>O Spotify é protegido, então o app busca a mesma música no YouTube e baixa em MP3.</span></div>}
        {!info.spotify && type === 'audio' && <div className="info-note"><Music2 size={17} /><span>O áudio será salvo em MP3 na melhor qualidade disponível.</span></div>}
        {running && job && <div className="progress-block"><div className="progress-label"><span>{job.message}</span><strong>{Math.round(job.percent)}%</strong></div><div className="progress-track"><div style={{ width: `${job.percent}%` }} /></div><p>Preparando o arquivo para você</p></div>}
        {job?.status === 'done' && <>
          <div className="success-note"><div className="status-icon"><Check size={16} /></div><div><strong>Download concluído</strong><span>{fileNameOf(job.filePath)}{job.fileSize ? ` · ${formatSize(job.fileSize)}` : ''}</span></div><button className="text-button" onClick={onOpenHistory}>Ver histórico</button></div>
          <div className="success-actions"><button className="ghost-button" onClick={showInFolder}><FolderOpen size={16} /> Mostrar na pasta</button><button className="ghost-button" onClick={() => { setLink(''); reset() }}><Download size={16} /> Baixar outro</button></div>
        </>}
        {job?.status === 'failed' && <div className="error-note"><AlertCircle size={17} /><span>{job.error ?? 'O download falhou.'}</span><button className="text-button" onClick={retry}>Tentar de novo</button></div>}
        {startError && <p className="error-line"><AlertCircle size={14} /> {startError}</p>}
        {(!job || job.status === 'downloading') && <button className="download-button" onClick={startDownload} disabled={running || !ready}><Download size={18} /> {running ? 'Baixando...' : 'Baixar agora'}</button>}
      </section> : <section className="empty-tip"><div className="tip-icon"><Download size={21} /></div><div><strong>Pronto para começar?</strong><p>Seu download vai aparecer aqui assim que você analisar um link.</p></div></section>}
    </div>
  )
}

function HistoryPage({ items, onUseLink, onOtherFormat, onGoDownload, refresh, toast, confirm }: {
  items: Entry[]
  onUseLink: (link: string) => void
  onOtherFormat: (entry: Entry) => void
  onGoDownload: () => void
  refresh: () => Promise<void>
  toast: (message: string) => void
  confirm: (options: ConfirmOptions) => void
}) {
  const [query, setQuery] = useState('')
  const [source, setSource] = useState('all')
  const [kind, setKind] = useState('all')
  const [state, setState] = useState('all')
  const [sort, setSort] = useState('recent')

  const filtered = useMemo(() => {
    const text = query.trim().toLowerCase()
    const list = items.filter((item) =>
      (!text || item.title.toLowerCase().includes(text) || (item.uploader ?? '').toLowerCase().includes(text)) &&
      (source === 'all' || item.platform === source) &&
      (kind === 'all' || item.type === kind) &&
      (state === 'all' || item.status === state))
    const order: Record<string, (a: Entry, b: Entry) => number> = {
      recent: (a, b) => b.createdAt.localeCompare(a.createdAt),
      oldest: (a, b) => a.createdAt.localeCompare(b.createdAt),
      name: (a, b) => a.title.localeCompare(b.title, 'pt-BR'),
      size: (a, b) => (b.fileSize ?? 0) - (a.fileSize ?? 0),
    }
    return [...list].sort(order[sort])
  }, [items, query, source, kind, state, sort])

  function clearHistory() {
    confirm({
      title: 'Limpar histórico',
      text: 'Os registros serão removidos da lista. Os arquivos baixados continuam nas suas pastas.',
      confirmLabel: 'Limpar histórico',
      danger: true,
      onConfirm: async () => {
        try { await api('/api/history/clear', {}); await refresh(); toast('Histórico limpo') } catch (error) { toast(errorText(error)) }
      },
    })
  }

  return <div className="page-stack history-page"><div className="page-title-row"><div><p className="eyebrow">arquivos</p><h1>Histórico</h1><p className="hero-subtitle">Arquivos processados pelo aplicativo.</p></div><button className="ghost-button" onClick={clearHistory} disabled={items.every((item) => item.status === 'downloading')}><Trash2 size={16} /> Limpar histórico</button></div>
    <div className="toolbar">
      <div className="input-wrap search-input"><Search size={17} /><input value={query} onChange={(event) => setQuery(event.target.value)} placeholder="Buscar por título ou autor" /></div>
      <MenuSelect label="Filtrar por fonte" value={source} options={[{ value: 'all', label: 'Todas as fontes' }, ...Object.entries(platformLabels).map(([value, label]) => ({ value, label }))]} onChange={setSource} />
      <MenuSelect label="Filtrar por tipo" value={kind} options={[{ value: 'all', label: 'Vídeo e áudio' }, { value: 'video', label: 'Só vídeo' }, { value: 'audio', label: 'Só áudio' }]} onChange={setKind} />
      <MenuSelect label="Filtrar por status" value={state} options={[{ value: 'all', label: 'Todos os status' }, { value: 'done', label: 'Concluídos' }, { value: 'downloading', label: 'Baixando' }, { value: 'failed', label: 'Com falha' }]} onChange={setState} />
      <MenuSelect label="Ordenar histórico" value={sort} options={[{ value: 'recent', label: 'Mais recentes' }, { value: 'oldest', label: 'Mais antigos' }, { value: 'name', label: 'Nome' }, { value: 'size', label: 'Tamanho' }]} onChange={setSort} />
    </div>
    <div className="history-list">{filtered.map((item) => <HistoryRow key={item.id} item={item} onUseLink={onUseLink} onOtherFormat={onOtherFormat} refresh={refresh} toast={toast} confirm={confirm} />)}</div>
    {items.length === 0 && <div className="empty-state"><History size={30} /><h3>Seus downloads aparecem aqui</h3><p>Analise um link e baixe seu primeiro arquivo.</p><button className="primary-button empty-action" onClick={onGoDownload}><Download size={16} /> Ir para Baixar</button></div>}
    {items.length > 0 && filtered.length === 0 && <div className="empty-state"><History size={30} /><h3>Nenhum resultado</h3><p>Tente buscar por outro título ou autor, ou mude os filtros.</p></div>}
  </div>
}

function HistoryRow({ item, onUseLink, onOtherFormat, refresh, toast, confirm }: {
  item: Entry
  onUseLink: (link: string) => void
  onOtherFormat: (entry: Entry) => void
  refresh: () => Promise<void>
  toast: (message: string) => void
  confirm: (options: ConfirmOptions) => void
}) {
  const [menuOpen, setMenuOpen] = useState(false)
  const ref = useRef<HTMLDivElement>(null)

  useEffect(() => {
    if (!menuOpen) return
    const close = (event: MouseEvent) => { if (!ref.current?.contains(event.target as Node)) setMenuOpen(false) }
    const escape = (event: KeyboardEvent) => { if (event.key === 'Escape') setMenuOpen(false) }
    document.addEventListener('mousedown', close)
    document.addEventListener('keydown', escape)
    return () => { document.removeEventListener('mousedown', close); document.removeEventListener('keydown', escape) }
  }, [menuOpen])

  const running = item.status === 'downloading'
  const failed = item.status === 'failed'
  const missing = item.status === 'done' && !item.fileExists
  const canUseFile = item.status === 'done' && item.fileExists
  const color = ({ youtube: 'red', instagram: 'orange', spotify: 'green' } as Record<string, string>)[item.platform] ?? 'dark'

  async function call(path: string, body: unknown = {}, success?: string) {
    setMenuOpen(false)
    try {
      await api(path, body)
      if (success) toast(success)
      await refresh()
    } catch (error) { toast(errorText(error)) }
  }

  function remove(deleteFile: boolean) {
    setMenuOpen(false)
    if (!deleteFile) { call(`/api/history/${item.id}/remove`, { deleteFile: false }, 'Removido do histórico'); return }
    confirm({
      title: 'Remover e apagar arquivo',
      text: `O arquivo "${fileNameOf(item.filePath)}" será apagado do seu computador. Essa ação não pode ser desfeita.`,
      confirmLabel: 'Remover e apagar',
      danger: true,
      onConfirm: () => call(`/api/history/${item.id}/remove`, { deleteFile: true }, 'Arquivo apagado'),
    })
  }

  const badge = running ? <Badge tone="info">Baixando {Math.round(item.percent)}%</Badge>
    : failed ? <Badge tone="danger">Falhou</Badge>
    : missing ? <Badge tone="warning">Arquivo não encontrado</Badge>
    : <Badge tone="success">Concluído</Badge>

  return <article className="history-row">
    <button className={`history-thumb ${color}`} disabled={!canUseFile} aria-label={`Abrir ${item.title}`} title={canUseFile ? 'Abrir arquivo' : undefined} onClick={() => call(`/api/history/${item.id}/open`)} style={item.thumb ? { backgroundImage: `linear-gradient(180deg, #0001, #0009), url("${item.thumb}")` } : undefined}>{running ? <LoaderCircle className="spin" size={19} /> : <Play size={19} fill="white" />}</button>
    <div className="history-main">
      <div className="history-title-line"><h3 title={item.title}>{item.title}</h3>
        <div className="history-menu" ref={ref}>
          <button className="icon-button" aria-label={`Mais ações para ${item.title}`} aria-expanded={menuOpen} onClick={() => setMenuOpen(!menuOpen)}><MoreHorizontal size={19} /></button>
          {menuOpen && <div className="history-menu-popover">
            {!running && <button onClick={() => { setMenuOpen(false); onUseLink(item.url) }}><Link2 size={15} /> Abrir no downloader</button>}
            {!running && <button disabled={!canUseFile} onClick={() => call(`/api/history/${item.id}/show`)}><FolderOpen size={15} /> Mostrar na pasta</button>}
            {!running && <button onClick={() => { setMenuOpen(false); onOtherFormat(item) }}><RotateCcw size={15} /> Baixar em outro formato</button>}
            {!running && <button disabled={!canUseFile} onClick={() => call(`/api/history/${item.id}/open`)}><ExternalLink size={15} /> Abrir arquivo</button>}
            <button onClick={async () => { setMenuOpen(false); await copyText(item.url); toast('Link copiado') }}><Copy size={15} /> Copiar link</button>
            {failed && <button onClick={() => call(`/api/history/${item.id}/retry`, {}, 'Download iniciado')}><RotateCcw size={15} /> Tentar de novo</button>}
            {!running && <div className="menu-separator" />}
            {!running && <button onClick={() => remove(false)}><Trash2 size={15} /> Remover do histórico</button>}
            {canUseFile && <button className="danger-item" onClick={() => remove(true)}><Trash2 size={15} /> Remover e apagar arquivo</button>}
          </div>}
        </div>
      </div>
      <p className="history-meta"><span>{platformLabel(item.platform)}</span>{item.uploader ? ` · ${item.uploader}` : ''}</p>
      <div className="history-bottom">{badge}<span>{formatLabel(item)}</span>{(item.fileSize || item.duration) ? <span>{[formatSize(item.fileSize), formatDuration(item.duration)].filter(Boolean).join(' · ')}</span> : null}<span>{formatDate(item.createdAt)}</span></div>
      {running && <div className="progress-track mini"><div style={{ width: `${item.percent}%` }} /></div>}
      {failed && item.error && <p className="error-line"><AlertCircle size={14} /> {item.error}</p>}
    </div>
  </article>
}

function FormatModal({ entry, settings, onClose, onStarted, toast }: {
  entry: Entry
  settings: Settings
  onClose: () => void
  onStarted: (entry: Entry) => void
  toast: (message: string) => void
}) {
  const [info, setInfo] = useState<Info | null>(null)
  const [loadError, setLoadError] = useState('')
  const [attempt, setAttempt] = useState(0)
  const [type, setType] = useState<'video' | 'audio'>(entry.type === 'video' ? 'audio' : 'video')
  const [quality, setQuality] = useState('0')
  const [dir, setDir] = useState(entry.outputDir)
  const [error, setError] = useState('')

  useEffect(() => {
    let cancelled = false
    setInfo(null)
    setLoadError('')
    api<Info>('/api/info', { url: entry.url, cookies: entry.cookies ?? settings.cookies })
      .then((result) => {
        if (cancelled) return
        setInfo(result)
        if (result.spotify || result.heights.length === 0) setType('audio')
      })
      .catch((failure) => { if (!cancelled) setLoadError(errorText(failure)) })
    return () => { cancelled = true }
  }, [entry.id, entry.url, entry.cookies, settings.cookies, attempt])

  const audioOnly = !!info && (info.spotify || info.heights.length === 0)
  const alreadyHeight = entry.type === 'video' ? (entry.height ?? entry.quality) : 0
  const qualityOptions: Option[] = info
    ? [
        { value: '0', label: `Melhor disponível${entry.type === 'video' && entry.quality === 0 ? ' · já baixado' : ''}` },
        ...info.heights.map((height) => ({ value: String(height), label: `${height}p${entry.type === 'video' && entry.quality !== 0 && height === alreadyHeight ? ' · já baixado' : ''}` })),
      ]
    : []

  async function choose() {
    try {
      const result = await api<{ path: string | null }>('/api/pick-folder', { initial: dir })
      if (result.path) setDir(result.path)
    } catch (failure) { toast(errorText(failure)) }
  }

  async function start() {
    setError('')
    try {
      const created = await api<Entry>('/api/download', {
        url: entry.url,
        kind: type,
        height: type === 'video' ? Number(quality) || 0 : 0,
        outputDir: dir,
        cookies: entry.cookies ?? settings.cookies,
      })
      onStarted(created)
      toast('Download iniciado')
      onClose()
    } catch (failure) { setError(errorText(failure)) }
  }

  return <Modal title="Baixar em outro formato" onClose={onClose}>
    <div className="preview-row">
      <div className="video-thumb" style={entry.thumb ? { backgroundImage: `linear-gradient(180deg, #0001, #0009), url("${entry.thumb}")` } : undefined}><Play size={22} fill="white" /></div>
      <div className="preview-copy"><h3>{entry.title}</h3><p>Baixado: {formatLabel(entry)}</p></div>
    </div>
    <div className="divider" />
    {!info && !loadError && <p className="modal-loading"><LoaderCircle className="spin" size={16} /> Buscando formatos...</p>}
    {loadError && <div className="error-note"><AlertCircle size={17} /><span>{loadError}</span><button className="text-button" onClick={() => setAttempt(attempt + 1)}>Tentar de novo</button></div>}
    {info && <>
      <div className="field-label">Novo formato</div>
      <div className="segmented">
        <button className={type === 'video' ? 'selected' : ''} disabled={audioOnly} onClick={() => setType('video')}><Video size={17} /> Vídeo <span>MP4</span></button>
        <button className={type === 'audio' ? 'selected' : ''} onClick={() => setType('audio')}><Music2 size={17} /> Áudio <span>MP3{entry.type === 'audio' ? ' · já baixado' : ''}</span></button>
      </div>
      {type === 'video' && <div className="field"><label>Qualidade do vídeo</label><div className="select-wrap"><MenuSelect label="Qualidade do vídeo" value={quality} options={qualityOptions} onChange={setQuality} /></div></div>}
      <div className="field"><label htmlFor="modal-folder">Salvar em</label><div className="input-wrap"><FolderOpen size={17} /><input id="modal-folder" value={dir} onChange={(event) => setDir(event.target.value)} /><button className="inline-action" onClick={choose}>Escolher</button></div></div>
      {error && <p className="error-line"><AlertCircle size={14} /> {error}</p>}
    </>}
    <div className="modal-actions"><button className="ghost-button" onClick={onClose}>Cancelar</button><button className="primary-button" onClick={start} disabled={!info}><Download size={16} /> Baixar</button></div>
  </Modal>
}

function SettingsPage({ settings, updateSettings, status, items, toast, confirm, refresh }: {
  settings: Settings
  updateSettings: (patch: Partial<Settings>) => Promise<void>
  status: ToolStatus | null
  items: Entry[]
  toast: (message: string) => void
  confirm: (options: ConfirmOptions) => void
  refresh: () => Promise<void>
}) {
  const [dirText, setDirText] = useState(settings.defaultDir)
  const [versions, setVersions] = useState<{ ytdlp: string; ffmpeg: string } | null>(null)
  const [updating, setUpdating] = useState(false)
  const [updateNote, setUpdateNote] = useState('')

  useEffect(() => setDirText(settings.defaultDir), [settings.defaultDir])
  useEffect(() => {
    if (status?.ready) api<{ ytdlp: string; ffmpeg: string }>('/api/tools').then(setVersions).catch(() => {})
  }, [status?.ready])

  async function chooseFolder() {
    try {
      const result = await api<{ path: string | null }>('/api/pick-folder', { initial: settings.defaultDir })
      if (result.path) await updateSettings({ defaultDir: result.path })
    } catch (error) { toast(errorText(error)) }
  }

  function changeRetention(value: string) {
    const days = Number(value)
    const hasRecords = items.some((item) => item.status !== 'downloading')
    if (days === -1 && hasRecords) {
      confirm({
        title: 'Não guardar histórico',
        text: 'O histórico atual será apagado e os próximos downloads não serão registrados. Os arquivos baixados continuam nas suas pastas.',
        confirmLabel: 'Apagar e não guardar',
        danger: true,
        onConfirm: () => updateSettings({ historyDays: days }),
      })
      return
    }
    updateSettings({ historyDays: days })
  }

  function clearHistory() {
    confirm({
      title: 'Limpar histórico',
      text: 'Os registros serão removidos da lista. Os arquivos baixados continuam nas suas pastas.',
      confirmLabel: 'Limpar histórico',
      danger: true,
      onConfirm: async () => {
        try { await api('/api/history/clear', {}); await refresh(); toast('Histórico limpo') } catch (error) { toast(errorText(error)) }
      },
    })
  }

  async function updateTools() {
    setUpdating(true)
    setUpdateNote('Verificando...')
    try {
      const result = await api<{ output: string; versions: { ytdlp: string; ffmpeg: string } }>('/api/tools/update', {})
      setVersions(result.versions)
      setUpdateNote(/up to date/i.test(result.output) ? 'Já está na versão mais recente.' : /updated/i.test(result.output) ? 'Atualizado com sucesso.' : 'Verificação concluída.')
    } catch (error) { setUpdateNote(errorText(error)) } finally { setUpdating(false) }
  }

  const hasQuality = qualityPresets.some((option) => option.value === String(settings.defaultQuality))
  const qualityOptions = hasQuality ? qualityPresets : [...qualityPresets, { value: String(settings.defaultQuality), label: `${settings.defaultQuality}p` }]

  return <div className="page-stack settings-page"><div className="page-title-row"><div><p className="eyebrow">do seu jeito</p><h1>Configurações</h1><p className="hero-subtitle">Ajuste o Jaca para funcionar como você gosta.</p></div></div><div className="settings-grid"><div className="settings-column">
    <SettingsSection title="Downloads" icon={<Download size={17} />}>
      <SettingField label="Pasta padrão"><div className="input-wrap"><FolderOpen size={16} /><input value={dirText} onChange={(event) => setDirText(event.target.value)} onBlur={() => { if (dirText.trim() && dirText !== settings.defaultDir) updateSettings({ defaultDir: dirText }) }} onKeyDown={(event) => { if (event.key === 'Enter') (event.target as HTMLInputElement).blur() }} /><button className="inline-action" onClick={chooseFolder}>Escolher</button></div></SettingField>
      <SettingField label="Formato padrão"><div className="segmented compact"><button className={settings.defaultType === 'video' ? 'selected' : ''} onClick={() => updateSettings({ defaultType: 'video' })}><Video size={15} /> Vídeo</button><button className={settings.defaultType === 'audio' ? 'selected' : ''} onClick={() => updateSettings({ defaultType: 'audio' })}><Music2 size={15} /> Áudio</button></div></SettingField>
      <SettingField label="Qualidade de vídeo"><div className="select-wrap"><MenuSelect label="Qualidade padrão" value={String(settings.defaultQuality)} options={qualityOptions} onChange={(value) => updateSettings({ defaultQuality: Number(value) })} /></div></SettingField>
      <SettingField label="Login do navegador"><div className="select-wrap"><MenuSelect label="Login do navegador" value={settings.cookies} options={browserOptions} onChange={(value) => updateSettings({ cookies: value })} /></div><p className="field-hint">Ajuda em posts que pedem login, como Instagram e X.</p></SettingField>
    </SettingsSection>
    <SettingsSection title="Aparência" icon={<Sparkles size={17} />}>
      <SettingField label="Tema"><div className="segmented compact"><button className={settings.theme === 'system' ? 'selected' : ''} onClick={() => updateSettings({ theme: 'system' as Theme })}>Automático</button><button className={settings.theme === 'light' ? 'selected' : ''} onClick={() => updateSettings({ theme: 'light' as Theme })}>Claro</button><button className={settings.theme === 'dark' ? 'selected' : ''} onClick={() => updateSettings({ theme: 'dark' as Theme })}>Escuro</button></div></SettingField>
    </SettingsSection>
  </div><div className="settings-column">
    <SettingsSection title="Histórico" icon={<History size={17} />}>
      <SettingField label="Manter histórico"><div className="select-wrap"><MenuSelect label="Manter histórico" value={String(settings.historyDays)} options={retentionOptions} onChange={changeRetention} /></div></SettingField>
      <button className="ghost-button full" onClick={clearHistory}><Trash2 size={16} /> Limpar histórico</button>
    </SettingsSection>
    <SettingsSection title="Ferramentas" icon={<SlidersHorizontal size={17} />}>
      <div className="tool-versions"><div><span>Motor de download (yt-dlp)</span><strong>{versions?.ytdlp ?? '—'}</strong></div><div><span>ffmpeg</span><strong>{versions?.ffmpeg ?? '—'}</strong></div></div>
      <button className="ghost-button full" onClick={updateTools} disabled={updating || !status?.ready}>{updating ? <LoaderCircle className="spin" size={16} /> : <RotateCcw size={16} />} Verificar atualização</button>
      {updateNote && <p className="field-hint">{updateNote}</p>}
      <p className="legal">Atualizar o motor corrige downloads que param de funcionar quando um site muda.</p>
    </SettingsSection>
    <SettingsSection title="Sobre o Jaca" icon={<Sparkles size={17} />}><div className="about"><Logo small /><div><strong>Jaca Downloader</strong><span>Versão {status?.version ?? ''}</span></div></div><p className="legal">Baixe apenas conteúdo que você tem permissão para baixar.</p></SettingsSection>
  </div></div></div>
}

function SettingField({ label, children }: { label: string; children: React.ReactNode }) { return <div className="setting-field"><label>{label}</label>{children}</div> }
function SettingsSection({ title, icon, children }: { title: string; icon: React.ReactNode; children: React.ReactNode }) { return <section className="card settings-section"><div className="section-heading"><h2>{icon}{title}</h2></div><div className="settings-content">{children}</div></section> }

export default function Page() {
  const [tab, setTab] = useState<Tab>('download')
  const [settings, setSettings] = useState<Settings | null>(null)
  const [status, setStatus] = useState<ToolStatus | null>(null)
  const [statusNonce, setStatusNonce] = useState(0)
  const [update, setUpdate] = useState<{ version: string | null; installing: boolean } | null>(null)
  const [history, setHistory] = useState<Entry[]>([])
  const [request, setRequest] = useState<{ url: string; nonce: number }>()
  const [modal, setModal] = useState<ModalState | null>(null)
  const [toastMessage, setToastMessage] = useState<string | null>(null)
  const toastTimer = useRef<number | undefined>(undefined)

  const toast = useCallback((message: string) => {
    setToastMessage(message)
    window.clearTimeout(toastTimer.current)
    toastTimer.current = window.setTimeout(() => setToastMessage(null), 2800)
  }, [])

  const refresh = useCallback(async () => {
    try { setHistory((await api<{ items: Entry[] }>('/api/history')).items) } catch { }
  }, [])

  useEffect(() => {
    api<Settings>('/api/settings').then((loaded) => { setSettings(loaded); setTab(loaded.lastTab) }).catch(() => {})
  }, [])

  useEffect(() => {
    let stopped = false
    let timer: number | undefined
    const poll = async () => {
      try {
        const next = await api<ToolStatus>('/api/status')
        if (stopped) return
        setStatus(next)
        if (!next.ready && !next.error) timer = window.setTimeout(poll, 1200)
      } catch { timer = window.setTimeout(poll, 1500) }
    }
    poll()
    return () => { stopped = true; window.clearTimeout(timer) }
  }, [statusNonce])

  // The app looks for a new version in the background, so the answer is polled until one shows up.
  useEffect(() => {
    if (update?.version) return
    const check = () => api<{ version: string | null; installing: boolean }>('/api/update').then(setUpdate).catch(() => {})
    const first = window.setTimeout(check, 4000)
    const timer = window.setInterval(check, 60000)
    return () => { window.clearTimeout(first); window.clearInterval(timer) }
  }, [update?.version])

  async function installUpdate() {
    setUpdate((current) => current && { ...current, installing: true })
    try { await api('/api/update/install', {}) } catch (error) {
      setUpdate((current) => current && { ...current, installing: false })
      toast(errorText(error))
    }
  }

  useEffect(() => { refresh() }, [refresh, tab])
  useEffect(() => {
    window.addEventListener('focus', refresh)
    return () => window.removeEventListener('focus', refresh)
  }, [refresh])

  const hasActive = history.some((entry) => entry.status === 'downloading')
  useEffect(() => {
    if (!hasActive) return
    const timer = window.setInterval(refresh, 900)
    return () => window.clearInterval(timer)
  }, [hasActive, refresh])

  const theme = settings?.theme
  useEffect(() => {
    if (!theme) return
    const media = window.matchMedia('(prefers-color-scheme: light)')
    const apply = () => {
      const resolved = theme === 'system' ? (media.matches ? 'light' : 'dark') : theme
      document.documentElement.dataset.theme = resolved
      hostMessage(`theme:${resolved}`)
    }
    apply()
    media.addEventListener('change', apply)
    return () => media.removeEventListener('change', apply)
  }, [theme])

  async function updateSettings(patch: Partial<Settings>) {
    if (!settings) return
    const previous = settings
    setSettings({ ...settings, ...patch })
    try {
      setSettings(await api<Settings>('/api/settings', { ...settings, ...patch }))
      if (patch.historyDays !== undefined) refresh()
    } catch (error) {
      setSettings(previous)
      toast(errorText(error))
    }
  }

  function go(next: Tab) {
    setTab(next)
    updateSettings({ lastTab: next })
  }

  function started(entry: Entry) {
    setHistory((current) => [entry, ...current.filter((item) => item.id !== entry.id)])
  }

  function useHistoryLink(url: string) {
    setRequest({ url, nonce: Date.now() })
    go('download')
  }

  async function retryTools() {
    await api('/api/tools/retry', {}).catch(() => {})
    setStatusNonce((value) => value + 1)
  }

  const confirm = useCallback((options: ConfirmOptions) => {
    setModal({ kind: 'confirm', ...options })
  }, [])
  const closeModal = useCallback(() => setModal(null), [])

  const ready = !!status?.ready
  const activeCount = history.filter((entry) => entry.status === 'downloading').length

  // Each tab starts at the top of the scrolling content area.
  const contentRef = useRef<HTMLDivElement>(null)
  useEffect(() => { contentRef.current?.scrollTo({ top: 0 }) }, [tab])

  return <main className="app-shell">
    <header className="app-header"><div className="brand"><Logo small /><div><strong>Jaca Downloader</strong></div></div><WindowControls /></header>
    <div className="app-body">
      <nav className="tabs" aria-label="Navegação principal">
        <button aria-label="Baixar" title="Baixar" className={tab === 'download' ? 'active' : ''} onClick={() => go('download')}><Download size={18} /></button>
        <button aria-label="Histórico" title="Histórico" className={tab === 'history' ? 'active' : ''} onClick={() => go('history')}><History size={18} />{history.length > 0 && <span className="tab-count">{activeCount > 0 ? activeCount : history.length}</span>}</button>
        <button className={`settings-tab ${tab === 'settings' ? 'active' : ''}`} aria-label="Configurações" title="Configurações" onClick={() => go('settings')}><SettingsIcon size={17} /></button>
      </nav>
      <div className="main-col">
      <div className="content" ref={contentRef}>
        {status && !status.ready && <div className={`tools-banner ${status.error ? 'failed' : ''}`}>{status.error ? <AlertCircle size={18} /> : <LoaderCircle className="spin" size={18} />}<span>{status.error ? `${status.message}: ${status.error}` : status.message}</span>{status.error && <button className="text-button" onClick={retryTools}>Tentar de novo</button>}</div>}
        {update?.version && <div className="tools-banner"><Sparkles size={18} /><span>{update.installing ? 'Atualizando o Jaca Downloader...' : `Nova versão disponível: ${update.version}`}</span>{update.installing ? <LoaderCircle className="spin" size={18} /> : <button className="text-button" onClick={installUpdate}>Atualizar agora</button>}</div>}
        {settings && <>
          <div hidden={tab !== 'download'}><DownloadPage settings={settings} ready={ready} history={history} request={request} onStarted={started} onOpenHistory={() => go('history')} toast={toast} /></div>
          <div hidden={tab !== 'history'}><HistoryPage items={history} onUseLink={useHistoryLink} onOtherFormat={(entry) => setModal({ kind: 'format', entry })} onGoDownload={() => go('download')} refresh={refresh} toast={toast} confirm={confirm} /></div>
          <div hidden={tab !== 'settings'}><SettingsPage settings={settings} updateSettings={updateSettings} status={status} items={history} toast={toast} confirm={confirm} refresh={refresh} /></div>
        </>}
      </div>
      <footer><span>Jaca Downloader</span><span>© {new Date().getFullYear()} Crocodile Development. All rights reserved</span></footer>
      </div>
    </div>

    {modal?.kind === 'confirm' && <Modal title={modal.title} onClose={closeModal}>
      <p className="modal-text">{modal.text}</p>
      <div className="modal-actions"><button className="ghost-button" onClick={closeModal}>Cancelar</button><button className={modal.danger ? 'primary-button danger-button' : 'primary-button'} onClick={() => { const action = modal.onConfirm; closeModal(); action() }}>{modal.confirmLabel}</button></div>
    </Modal>}
    {modal?.kind === 'format' && settings && <FormatModal entry={modal.entry} settings={settings} onClose={closeModal} onStarted={started} toast={toast} />}
    {toastMessage && <div className="toast" role="status">{toastMessage}</div>}
  </main>
}
