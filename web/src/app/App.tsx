import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { MotionConfig } from "framer-motion";
import { BrowserRouter, Route, Routes } from "react-router-dom";

import { DeadLettersPage } from "../features/dead-letters/DeadLettersPage";
import { DriftPage } from "../features/drift/DriftPage";
import { MerchantPage } from "../features/merchant/MerchantPage";
import { MerchantsPage } from "../features/merchants/MerchantsPage";
import { OverviewPage } from "../features/overview/OverviewPage";
import { ApiError } from "../shared/api/client";
import { SessionProvider } from "../shared/auth/SessionContext";
import { EmptyState } from "../shared/ui";
import { Layout } from "./Layout";

export function AppRoutes() {
  return (
    <SessionProvider>
      <Routes>
        <Route element={<Layout />}>
          <Route index element={<OverviewPage />} />
          <Route path="merchants" element={<MerchantsPage />} />
          <Route path="merchants/:merchantId" element={<MerchantPage />} />
          <Route path="dead-letters" element={<DeadLettersPage />} />
          <Route path="drift" element={<DriftPage />} />
          <Route
            path="*"
            element={<EmptyState title="Nothing here">This address is not part of the console.</EmptyState>}
          />
        </Route>
      </Routes>
    </SessionProvider>
  );
}

const client = new QueryClient({
  defaultOptions: {
    queries: {
      // Retry what may pass on its own (network, 429, 5xx); a 4xx will not change by asking again.
      retry: (failures, error) => failures < 3 && (!(error instanceof ApiError) || error.retryable),
      refetchOnWindowFocus: true,
    },
  },
});

export function App() {
  return (
    <QueryClientProvider client={client}>
      <MotionConfig reducedMotion="user">
        <BrowserRouter>
          <AppRoutes />
        </BrowserRouter>
      </MotionConfig>
    </QueryClientProvider>
  );
}
