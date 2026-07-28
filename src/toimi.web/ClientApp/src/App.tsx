import { BrowserRouter, Route, Routes } from 'react-router-dom'
import { ToimiView } from './components/ToimiView.tsx'
import { AdminLayout } from './admin/AdminLayout.tsx'
import { DashboardPage } from './admin/DashboardPage.tsx'
import { DataPage } from './admin/DataPage.tsx'
import { EntityDetailPage } from './admin/EntityDetailPage.tsx'
import { TypesPage } from './admin/TypesPage.tsx'
import { TypeDetailPage } from './admin/TypeDetailPage.tsx'
import { UsagePage } from './admin/UsagePage.tsx'

export default function App() {
  return (
    <BrowserRouter>
      <Routes>
        <Route path="/" element={<ToimiView />} />
        <Route path="/admin" element={<AdminLayout />}>
          <Route index element={<DashboardPage />} />
          <Route path="data" element={<DataPage />} />
          <Route path="data/:id" element={<EntityDetailPage />} />
          <Route path="types" element={<TypesPage />} />
          <Route path="types/:name" element={<TypeDetailPage />} />
          <Route path="usage" element={<UsagePage />} />
        </Route>
      </Routes>
    </BrowserRouter>
  )
}
