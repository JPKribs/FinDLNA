document.addEventListener('DOMContentLoaded', function() {
    const form = document.getElementById('loginForm');
    const status = document.getElementById('status');
    
    checkAuthStatus();
    
    form.addEventListener('submit', async function(e) {
        e.preventDefault();
        
        const serverUrl = document.getElementById('serverUrl').value;
        const username = document.getElementById('username').value;
        const password = document.getElementById('password').value;
        
        const submitButton = form.querySelector('button');
        submitButton.disabled = true;
        submitButton.textContent = 'Connecting...';
        
        try {
            const requestBody = {
                serverUrl: serverUrl,
                username: username
            };
            
            if (password && password.trim() !== '') {
                requestBody.password = password;
            }
            
            const response = await fetch('/api/auth/login', {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/json'
                },
                body: JSON.stringify(requestBody)
            });
            
            const result = await response.json();
            
            if (result.success) {
                showStatus('Authentication successful! Starting DLNA server...', 'success');
                setTimeout(() => {
                    window.location.reload();
                }, 2000);
            } else {
                showStatus('Error: ' + result.error, 'error');
            }
        } catch (error) {
            showStatus('Connection failed: ' + error.message, 'error');
        } finally {
            submitButton.disabled = false;
            submitButton.textContent = 'Connect';
        }
    });
    
    async function checkAuthStatus() {
        try {
            const response = await fetch('/api/auth/status');
            const result = await response.json();
            
            if (result.configured) {
                showConfiguredState(result.serverUrl);
            }
        } catch (error) {
            console.log('Status check failed:', error);
        }
    }
    
    // MARK: showConfiguredState
    function showConfiguredState(serverUrl) {
        const container = document.querySelector('.container');
        container.innerHTML = `
            <h1>FinDLNA Server</h1>
            <div class="info-box">
                <h3>Current Configuration</h3>
                <p><strong>Server:</strong> ${serverUrl}</p>
                <p><strong>Status:</strong> DLNA Server Running</p>
            </div>
            <button id="reconfigureBtn" class="reconfigure-btn">Reconfigure</button>
        `;
        
        document.getElementById('reconfigureBtn').addEventListener('click', function() {
            if (confirm('Reset configuration and reconnect?')) {
                showLoginForm();
            }
        });
    }
    
    // MARK: showLoginForm
    function showLoginForm() {
        const container = document.querySelector('.container');
        container.innerHTML = `
            <h1>FinDLNA Server Setup</h1>
            <form id="loginForm">
                <input type="url" id="serverUrl" placeholder="Jellyfin Server URL" required>
                <input type="text" id="username" placeholder="Username" required>
                <input type="password" id="password" placeholder="Password (optional)">
                <button type="submit">Connect</button>
            </form>
            <div id="status"></div>
        `;
        
        const newForm = document.getElementById('loginForm');
        const newStatus = document.getElementById('status');
        
        newForm.addEventListener('submit', async function(e) {
            e.preventDefault();
            
            const serverUrl = document.getElementById('serverUrl').value;
            const username = document.getElementById('username').value;
            const password = document.getElementById('password').value;
            
            const submitButton = newForm.querySelector('button');
            submitButton.disabled = true;
            submitButton.textContent = 'Connecting...';
            
            try {
                const requestBody = {
                    serverUrl: serverUrl,
                    username: username
                };
                
                if (password && password.trim() !== '') {
                    requestBody.password = password;
                }
                
                const response = await fetch('/api/auth/login', {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json'
                    },
                    body: JSON.stringify(requestBody)
                });
                
                const result = await response.json();
                
                if (result.success) {
                    newStatus.textContent = 'Authentication successful! Starting DLNA server...';
                    newStatus.className = 'success';
                    newStatus.style.display = 'block';
                    setTimeout(() => {
                        checkAuthStatus();
                    }, 2000);
                } else {
                    newStatus.textContent = 'Error: ' + result.error;
                    newStatus.className = 'error';
                    newStatus.style.display = 'block';
                }
            } catch (error) {
                newStatus.textContent = 'Connection failed: ' + error.message;
                newStatus.className = 'error';
                newStatus.style.display = 'block';
            } finally {
                submitButton.disabled = false;
                submitButton.textContent = 'Connect';
            }
        });
    }
    
    function showStatus(message, type) {
        status.textContent = message;
        status.className = type;
        status.style.display = 'block';
    }
});