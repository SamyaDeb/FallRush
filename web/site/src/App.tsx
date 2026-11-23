import { useEffect } from 'react';
import { Navigate, Route, Routes, useLocation } from 'react-router-dom';
import Home from './pages/Home';
import Join from './pages/Join';
import Privacy from './pages/Privacy';
import { captureRef } from './lib/waitlist';

export default function App() {
  const loc = useLocation();
  // ?ref=CODE on any page: keep it in sessionStorage until the visitor joins
  useEffect(() => captureRef(loc.search), [loc.search]);
  return (
    <Routes>
      <Route path="/" element={<Home />} />
      <Route path="/join" element={<Join />} />
      <Route path="/privacy" element={<Privacy />} />
      <Route path="*" element={<Navigate to="/" replace />} />
    </Routes>
  );
}
