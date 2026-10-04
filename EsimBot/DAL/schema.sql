CREATE TABLE IF NOT EXISTS app_state (
    `key` VARCHAR(96) CHARACTER SET ascii COLLATE ascii_bin NOT NULL PRIMARY KEY,
    `value` TEXT NOT NULL
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS orders (
    id CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL PRIMARY KEY,
    user_id BIGINT NOT NULL,
    request_key VARCHAR(96) CHARACTER SET ascii COLLATE ascii_bin NOT NULL UNIQUE,
    package_code VARCHAR(128) NOT NULL,
    package_name VARCHAR(256) NOT NULL,
    cost_units BIGINT NOT NULL,
    stars INT NOT NULL,
    state VARCHAR(24) CHARACTER SET ascii COLLATE ascii_bin NOT NULL DEFAULT 'quoted',
    provider_order_no VARCHAR(128) CHARACTER SET ascii COLLATE ascii_bin NULL UNIQUE,
    terms_version VARCHAR(64) NOT NULL,
    created_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    expires_at DATETIME(6) NOT NULL,
    updated_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6) ON UPDATE CURRENT_TIMESTAMP(6),
    attempts INT NOT NULL DEFAULT 0,
    next_attempt_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    last_error_code VARCHAR(80) NULL,
    delivered_at DATETIME(6) NULL,
    CONSTRAINT ck_order_user CHECK (user_id > 0),
    CONSTRAINT ck_order_price CHECK (cost_units > 0 AND stars > 0),
    CONSTRAINT ck_order_attempts CHECK (attempts >= 0),
    CONSTRAINT ck_order_state CHECK (state IN ('quoted','paid','provisioning','ready','refund_pending','refunded','manual_review')),
    INDEX ix_orders_user (user_id, created_at DESC),
    INDEX ix_orders_work (state, next_attempt_at)
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS payments (
    charge_id VARCHAR(255) CHARACTER SET ascii COLLATE ascii_bin NOT NULL PRIMARY KEY,
    order_id CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NULL,
    payer_id BIGINT NOT NULL,
    stars INT NOT NULL,
    state VARCHAR(24) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    accepted_at DATETIME(6) NULL,
    accepted_order_id CHAR(32) CHARACTER SET ascii COLLATE ascii_bin
        GENERATED ALWAYS AS (CASE WHEN accepted_at IS NOT NULL THEN order_id ELSE NULL END) STORED,
    created_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    refunded_at DATETIME(6) NULL,
    attempts INT NOT NULL DEFAULT 0,
    next_attempt_at DATETIME(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    CONSTRAINT fk_payments_order FOREIGN KEY (order_id) REFERENCES orders(id),
    CONSTRAINT ck_payment_values CHECK (payer_id > 0 AND stars > 0),
    CONSTRAINT ck_payment_accepted_order CHECK (state <> 'accepted' OR (order_id IS NOT NULL AND accepted_at IS NOT NULL)),
    CONSTRAINT ck_payment_binding CHECK (accepted_at IS NULL OR order_id IS NOT NULL),
    CONSTRAINT ck_payment_attempts CHECK (attempts >= 0),
    CONSTRAINT ck_payment_state CHECK (state IN ('accepted','refund_pending','refunded')),
    UNIQUE KEY ux_one_accepted_payment (accepted_order_id),
    INDEX ix_payments_work (state, next_attempt_at)
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS esims (
    order_id CHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL PRIMARY KEY,
    provider_esim_id VARCHAR(128) CHARACTER SET ascii COLLATE ascii_bin NOT NULL UNIQUE,
    iccid VARCHAR(32) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    activation_ciphertext TEXT CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
    apn VARCHAR(128) NULL,
    CONSTRAINT fk_esims_order FOREIGN KEY (order_id) REFERENCES orders(id)
) ENGINE=InnoDB;

INSERT INTO app_state (`key`, `value`) VALUES ('schema_version', '1')
ON DUPLICATE KEY UPDATE `value` = `value`;
