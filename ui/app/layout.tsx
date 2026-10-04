import type { Metadata, Viewport } from 'next'
import './globals.css'
import './jaca.css'

export const metadata: Metadata = {
  title: 'Jaca Downloader',
  description: 'Baixe vídeos e músicas sem complicação.',
  icons: {
    icon: '/logo.png',
  },
}

export const viewport: Viewport = {
  colorScheme: 'light dark',
}

export default function RootLayout({
  children,
}: Readonly<{
  children: React.ReactNode
}>) {
  return (
    <html lang="pt-BR" data-theme="dark" suppressHydrationWarning>
      <head>
        <script
          dangerouslySetInnerHTML={{
            __html:
              "document.documentElement.dataset.theme=matchMedia('(prefers-color-scheme: light)').matches?'light':'dark'",
          }}
        />
      </head>
      <body className="antialiased">{children}</body>
    </html>
  )
}
