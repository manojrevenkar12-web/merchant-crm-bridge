import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

import { api } from "../../shared/api/client";
import type { DriftItem, ReconcileReport } from "../../shared/api/types";
import { refresh } from "../../shared/theme/tokens";

export const useDrift = () =>
  useQuery({
    queryKey: ["drift"],
    queryFn: () => api.get<DriftItem[]>("/v1/drift"),
    refetchInterval: refresh.list,
  });

export function useReconcile() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: () => api.post<ReconcileReport>("/v1/reconcile"),
    onSuccess: () => void qc.invalidateQueries(),
  });
}
