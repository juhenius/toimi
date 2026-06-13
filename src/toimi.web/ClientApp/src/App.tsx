import { BrowserRouter, Route, Routes } from 'react-router-dom'
import { ToimiView } from './components/ToimiView.tsx'
import { AdminLayout } from './admin/AdminLayout.tsx'
import { DashboardPage } from './admin/DashboardPage.tsx'
import { MemoriesPage } from './admin/MemoriesPage.tsx'
import { RemindersPage } from './admin/RemindersPage.tsx'
import { SchedulesPage } from './admin/SchedulesPage.tsx'
import { ScheduleDetailPage } from './admin/ScheduleDetailPage.tsx'
import { SkillsPage } from './admin/SkillsPage.tsx'

export default function App() {
  return (
    <BrowserRouter>
      <Routes>
        <Route path="/" element={<ToimiView />} />
        <Route path="/admin" element={<AdminLayout />}>
          <Route index element={<DashboardPage />} />
          <Route path="muistio" element={<MemoriesPage />} />
          <Route path="muistutin" element={<RemindersPage />} />
          <Route path="ajastin" element={<SchedulesPage />} />
          <Route path="ajastin/:id" element={<ScheduleDetailPage />} />
          <Route path="taidot" element={<SkillsPage />} />
        </Route>
      </Routes>
    </BrowserRouter>
  )
}
