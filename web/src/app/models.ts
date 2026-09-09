export interface ConnectionRequest { server: string; database: string; }
export interface ConnectionTestResponse { ok: boolean; version: string | null; tableCount: number; error: string | null; }

export interface Issue { code: string; severity: 'Blocking' | 'Warning' | 'Info'; message: string; table: string | null; column: string | null; }

export type RuleKind = 'copy' | 'truncate' | 'concat' | 'split' | 'case' | 'constant';

export interface ColumnMapping {
  targetColumn: string;
  rule: RuleKind;
  expression: string;
  origin: 'ai' | 'human';
  confidence: number | null;
  reason: string | null;
}

export interface UnmappedColumn { targetColumn: string; reason: string; }

export interface TableMapping {
  sourceTable: string;
  targetTable: string;
  origin: 'ai' | 'human';
  confidence: number | null;
  reason: string | null;
  columns: ColumnMapping[];
  unmapped: UnmappedColumn[];
}

export interface Mapping {
  name: string;
  model: string | null;
  sourceDatabase: string;
  sourceReference: string;
  targetDatabase: string;
  tables: TableMapping[];
}

export interface SessionStatus {
  state: 'running' | 'ready' | 'failed';
  step: string | null;
  mapping: Mapping | null;
  issues: Issue[];
  failures: string[];
  unmatchedSourceTables: string[];
  error: string | null;
}

export interface ValidateExpressionResponse { ok: boolean; resultType: string | null; issues: Issue[]; }
export interface GenerateScriptResponse { sql: string; issues: Issue[]; }
export interface SaveMappingResponse { path: string; sha256: string; }
