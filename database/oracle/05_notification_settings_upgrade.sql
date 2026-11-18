-- Additive and repeatable. Existing accounts, notifications and deliveries are retained.
DECLARE
    existing_table NUMBER;
BEGIN
    SELECT COUNT(*) INTO existing_table
    FROM ALL_TABLES
    WHERE OWNER = SYS_CONTEXT('USERENV', 'CURRENT_SCHEMA')
        AND TABLE_NAME = 'NOTIFICATION_CHANNEL_SETTING';

    IF existing_table = 0 THEN
        EXECUTE IMMEDIATE q'[
            CREATE TABLE NOTIFICATION_CHANNEL_SETTING (
                USER_ID NUMBER(19,0) NOT NULL,
                EVENT_KIND VARCHAR2(40 CHAR) NOT NULL,
                EMAIL_MODE VARCHAR2(10 CHAR) NOT NULL,
                CHAT_MODE VARCHAR2(10 CHAR) NOT NULL,
                VERSION NUMBER(19,0) DEFAULT 1 NOT NULL,
                UPDATED_BY NUMBER(19,0) NOT NULL,
                UPDATED_AT_UTC TIMESTAMP(6)
                    DEFAULT SYS_EXTRACT_UTC(SYSTIMESTAMP) NOT NULL,
                CONSTRAINT PK_NCS PRIMARY KEY (USER_ID, EVENT_KIND),
                CONSTRAINT FK_NCS_USER FOREIGN KEY (USER_ID)
                    REFERENCES USER_ACCOUNT (USER_ID),
                CONSTRAINT FK_NCS_ACTOR FOREIGN KEY (UPDATED_BY)
                    REFERENCES USER_ACCOUNT (USER_ID),
                CONSTRAINT CK_NCS_EMAIL CHECK (EMAIL_MODE IN ('OFF','MOCK','LOCAL')),
                CONSTRAINT CK_NCS_CHAT CHECK (CHAT_MODE IN ('OFF','MOCK','LOCAL')),
                CONSTRAINT CK_NCS_VERSION CHECK (VERSION > 0),
                CONSTRAINT CK_NCS_EVENT CHECK (EVENT_KIND IN (
                    'ALARM_OPENED',
                    'ALARM_RECOVERED',
                    'ALARM_CLOSED',
                    'ALARM_FORCE_CLOSED',
                    'AI_COMPLETED',
                    'AI_SCHEDULED'
                ))
            )
        ]';
    END IF;
END;
/
