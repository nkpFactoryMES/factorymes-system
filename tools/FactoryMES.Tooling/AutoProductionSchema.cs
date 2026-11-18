using FactoryMES.Backend.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FactoryMES.Tooling;

// 기존 DB는 초기화하지 않는다. 출처 기본값으로 수동 원장을 보존하고 새 AUTO 상태/관계만 추가한다.
public static class AutoProductionSchema
{
    public static async Task ApplyAsync(MesDbContext db)
    {
        await db.Database.ExecuteSqlRawAsync("""
            DECLARE
            n NUMBER;
            BEGIN
            SELECT COUNT(*) INTO n
            FROM USER_TAB_COLUMNS
            WHERE TABLE_NAME='PRODUCTION_ENTRY'
            
            AND COLUMN_NAME='ENTRY_SOURCE';
            IF n=0 THEN
            EXECUTE IMMEDIATE q'[ALTER TABLE PRODUCTION_ENTRY
            ADD ENTRY_SOURCE VARCHAR2(10 CHAR) DEFAULT 'MANUAL' NOT NULL]';
            END IF;
            END;
            """);
        await db.Database.ExecuteSqlRawAsync("""
            DECLARE
            n NUMBER;
            BEGIN
            SELECT COUNT(*) INTO n
            FROM USER_TAB_COLUMNS
            WHERE TABLE_NAME='PRODUCTION_ENTRY'
            
            AND COLUMN_NAME='TELEMETRY_ID';
            IF n=0 THEN
            EXECUTE IMMEDIATE q'[ALTER TABLE PRODUCTION_ENTRY
            ADD TELEMETRY_ID NUMBER(19,0)]';
            END IF;
            END;
            """);
        await db.Database.ExecuteSqlRawAsync("""
            DECLARE
            n NUMBER;
            BEGIN
            SELECT COUNT(*) INTO n
            FROM USER_TABLES
            WHERE TABLE_NAME='AUTO_COUNTER_STATE';
            IF n=0 THEN
            EXECUTE IMMEDIATE q'[CREATE TABLE AUTO_COUNTER_STATE (
            order_id NUMBER(19,0) NOT NULL,
            last_telemetry_id NUMBER(19,0),
            last_stream_id VARCHAR2(64 CHAR),
            last_sequence_no NUMBER(19,0),
            last_counter NUMBER(18,6),
            last_sample_at_utc TIMESTAMP(6),
            last_received_at_utc TIMESTAMP(6),
            accept_after_utc TIMESTAMP(6) NOT NULL,
            awaiting_baseline NUMBER(1,0) DEFAULT 1 NOT NULL,
            config_version NUMBER(12,0) NOT NULL,
            updated_at_utc TIMESTAMP(6) DEFAULT SYS_EXTRACT_UTC(SYSTIMESTAMP) NOT NULL,
            CONSTRAINT PK_AUTO_STATE PRIMARY KEY (order_id),
            CONSTRAINT FK_AUTO_ORDER
            FOREIGN KEY (order_id)
            REFERENCES WORK_ORDER(order_id),
            CONSTRAINT FK_AUTO_TELEM
            FOREIGN KEY (last_telemetry_id)
            REFERENCES TELEMETRY_EVENT(telemetry_id),
            CONSTRAINT CK_AUTO_BASE
            CHECK (awaiting_baseline IN (0,1)),
            CONSTRAINT CK_AUTO_CONFIG
            CHECK (config_version>0),
            CONSTRAINT CK_AUTO_RAW
            CHECK (last_counter IS NULL
            OR (last_counter>=0
            AND last_counter=TRUNC(last_counter))),
            CONSTRAINT CK_AUTO_SEQ
            CHECK (last_sequence_no IS NULL
            OR last_sequence_no>=0),
            CONSTRAINT CK_AUTO_COMPLETE
            CHECK (awaiting_baseline=1 OR
            (last_telemetry_id IS NOT NULL
            AND last_stream_id IS NOT NULL
            AND last_sequence_no IS NOT NULL
            
            AND last_counter IS NOT NULL
            AND last_sample_at_utc IS NOT NULL
            AND last_received_at_utc IS NOT NULL))
            )]';
            END IF;
            END;
            """);
        await db.Database.ExecuteSqlRawAsync("""
            DECLARE
            n NUMBER;
            BEGIN
            SELECT COUNT(*) INTO n
            FROM USER_CONSTRAINTS
            WHERE CONSTRAINT_NAME='FK_ENTRY_TELEM';
            IF n=0 THEN
            EXECUTE IMMEDIATE q'[ALTER TABLE PRODUCTION_ENTRY
            ADD CONSTRAINT FK_ENTRY_TELEM
            FOREIGN KEY (TELEMETRY_ID)
            REFERENCES TELEMETRY_EVENT(TELEMETRY_ID)]';
            END IF;
            END;
            """);
        await db.Database.ExecuteSqlRawAsync("""
            DECLARE
            n NUMBER;
            BEGIN
            SELECT COUNT(*) INTO n
            FROM USER_CONSTRAINTS
            WHERE CONSTRAINT_NAME='CK_ENTRY_SOURCE';
            IF n=0 THEN
            EXECUTE IMMEDIATE q'[ALTER TABLE PRODUCTION_ENTRY
            ADD CONSTRAINT CK_ENTRY_SOURCE
            CHECK ((entry_source='MANUAL'
            AND telemetry_id IS NULL)
            OR (entry_source='AUTO'
            AND kind='TOTAL'
            AND telemetry_id IS NOT NULL
            AND reversal_of IS NULL
            AND correction_id IS NULL))]';
            END IF;
            END;
            """);
        await db.Database.ExecuteSqlRawAsync("""
            DECLARE
            n NUMBER;
            old_count NUMBER;
            BEGIN
            SELECT COUNT(*) INTO n
            FROM USER_CONSTRAINTS
            WHERE CONSTRAINT_NAME='CK_WO_AUTO_ENDPOINTS';
            IF n=0 THEN
            SELECT COUNT(*) INTO old_count
            FROM USER_CONSTRAINTS
            WHERE CONSTRAINT_NAME='CK_WO_AUTO_END';
            IF old_count>0 THEN
            EXECUTE IMMEDIATE 'ALTER TABLE WORK_ORDER DROP CONSTRAINT CK_WO_AUTO_END';
            END IF;
            EXECUTE IMMEDIATE q'[ALTER TABLE WORK_ORDER
            ADD CONSTRAINT CK_WO_AUTO_ENDPOINTS
            CHECK (count_mode<>'AUTO'
            OR status NOT IN ('SUBMITTED','CLOSED')
            OR (start_counter IS NOT NULL
            AND end_counter IS NOT NULL
            AND start_sample_at_utc IS NOT NULL
            AND end_sample_at_utc IS NOT NULL
            AND start_received_at_utc IS NOT NULL
            AND end_received_at_utc IS NOT NULL))]';
            END IF;
            END;
            """);
        await db.Database.ExecuteSqlRawAsync("""
            DECLARE
            n NUMBER;
            BEGIN
            SELECT COUNT(*) INTO n
            FROM USER_INDEXES
            WHERE INDEX_NAME='IX_AUTO_TELEM';
            IF n=0 THEN
            EXECUTE IMMEDIATE q'[CREATE INDEX IX_AUTO_TELEM ON AUTO_COUNTER_STATE(LAST_TELEMETRY_ID)]';
            END IF;
            END;
            """);
        await db.Database.ExecuteSqlRawAsync("""
            DECLARE
            n NUMBER;
            BEGIN
            SELECT COUNT(*) INTO n
            FROM USER_INDEXES
            WHERE INDEX_NAME='IX_ENTRY_TELEM';
            IF n=0 THEN
            EXECUTE IMMEDIATE q'[CREATE INDEX IX_ENTRY_TELEM ON PRODUCTION_ENTRY(TELEMETRY_ID)]';
            END IF;
            END;
            """);
    }
}
