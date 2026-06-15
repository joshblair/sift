ALTER TABLE documents
  DROP CONSTRAINT documents_file_type_check,
  ADD  CONSTRAINT documents_file_type_check
       CHECK (file_type IN ('pdf', 'csv', 'docx', 'txt', 'md'));
